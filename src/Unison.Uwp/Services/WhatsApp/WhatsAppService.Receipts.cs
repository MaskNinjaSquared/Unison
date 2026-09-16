using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Unison.Uwp.Client;
using Unison.Core.Helpers;
using Unison.Core.Mappers;
using Unison.Core.Models;
using Unison.Baileys.Protocol;
using Unison.Uwp.Data;
using Unison.Baileys.Crypto;
using Unison.Uwp.Transport;
using Proto;
using Google.Protobuf;
using Windows.UI.Core;
using System.Threading;
using Windows.Storage;
using Windows.ApplicationModel.Core;
using Windows.Networking.Sockets;
using System.Runtime.InteropServices.WindowsRuntime;

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unison.Background;
using Unison.Baileys.Diagnostics;
using Unison.Baileys.Client;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.State;
using Unison.Socket.UseCases.Contacts;
using Unison.Uwp.Helpers;
using Unison.Uwp.Services.WhatsApp.Messages;
using Microsoft.Extensions.DependencyInjection;

namespace Unison.Uwp.Services.WhatsApp
{
    public partial class WhatsAppService
    {

        private async Task HandleMessageReceiptSafelyAsync(BinaryNode node)
        {
            try
            {
                await HandleMessageReceiptAsync(node);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Receipt processing failed: {ex.Message}");
            }
        }

        private async Task HandleMessageReceiptAsync(BinaryNode node)
        {
            ReceiptFacts receipt = _receipts.Read(node);
            if (receipt == null) return;

            // A "sender" receipt is our own echo, so it applies to the message as a whole and
            // never needs the per-participant tally a group read does.
            if (!receipt.IsGroup ||
                string.Equals(receipt.Status, ChatMessage.StatusSent, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var id in receipt.MessageIds)
                {
                    await UpdateOutgoingMessageStatusAsync(id, receipt.Status);
                }
                return;
            }

            if (string.IsNullOrWhiteSpace(receipt.Participant) || IsSelfLinkedJid(receipt.Participant)) return;

            int expectedRecipients = await GetExpectedGroupRecipientCountAsync(receipt.ChatJid);
            if (expectedRecipients <= 0) return;

            foreach (var id in receipt.MessageIds)
            {
                string aggregateStatus = RegisterGroupReceipt(
                    id,
                    receipt.Participant,
                    receipt.Status,
                    expectedRecipients);
                if (!string.IsNullOrWhiteSpace(aggregateStatus))
                {
                    await UpdateOutgoingMessageStatusAsync(id, aggregateStatus);
                }
            }
        }

        private string RegisterGroupReceipt(
            string messageId,
            string participant,
            string status,
            int expectedRecipients)
        {
            if (string.IsNullOrWhiteSpace(messageId) ||
                string.IsNullOrWhiteSpace(participant) ||
                expectedRecipients <= 0)
            {
                return null;
            }

            lock (_messageStateLock)
            {
                if (!_groupReceiptStateByMessageId.TryGetValue(messageId, out var state))
                {
                    state = new GroupReceiptState();
                    _groupReceiptStateByMessageId[messageId] = state;
                }

                state.UpdatedUtc = DateTime.UtcNow;
                if (string.Equals(status, ChatMessage.StatusRead, StringComparison.OrdinalIgnoreCase))
                {
                    state.ReadParticipants.Add(participant);
                    state.DeliveredParticipants.Add(participant);
                }
                else if (string.Equals(status, ChatMessage.StatusDelivered, StringComparison.OrdinalIgnoreCase))
                {
                    state.DeliveredParticipants.Add(participant);
                }

                if (state.ReadParticipants.Count >= expectedRecipients)
                {
                    _groupReceiptStateByMessageId.Remove(messageId);
                    return ChatMessage.StatusRead;
                }

                if (state.DeliveredParticipants.Count >= expectedRecipients)
                {
                    return ChatMessage.StatusDelivered;
                }

                // Bound the receipt cache. Completed read entries are removed above;
                // stale entries are discarded if the user sends to many groups.
                if (_groupReceiptStateByMessageId.Count > 500)
                {
                    DateTime cutoff = DateTime.UtcNow.AddDays(-1);
                    var staleIds = _groupReceiptStateByMessageId
                        .Where(pair => pair.Value == null || pair.Value.UpdatedUtc < cutoff)
                        .Select(pair => pair.Key)
                        .Take(100)
                        .ToList();
                    foreach (var staleId in staleIds) _groupReceiptStateByMessageId.Remove(staleId);
                }
            }

            return null;
        }

        private async Task<int> GetExpectedGroupRecipientCountAsync(string groupJid)
        {
            string canonical = GetCanonicalJid(groupJid);
            if (string.IsNullOrWhiteSpace(canonical) || _socket == null) return 0;

            lock (_messageStateLock)
            {
                if (_groupRecipientCountByChat.TryGetValue(canonical, out var cached) &&
                    cached != null &&
                    DateTime.UtcNow - cached.FetchedUtc < TimeSpan.FromMinutes(30))
                {
                    return cached.RecipientCount;
                }
            }

            try
            {
                var response = await _socket.QueryGroupMetadataAsync(canonical);
                ApplyGroupSendPermissionsFromMetadata(response, canonical);

                int? counted = _receipts.CountRecipients(response);
                if (counted == null) return 0;

                int recipientCount = counted.Value;
                lock (_messageStateLock)
                {
                    _groupRecipientCountByChat[canonical] = new GroupRecipientCountCacheEntry
                    {
                        RecipientCount = recipientCount,
                        FetchedUtc = DateTime.UtcNow
                    };
                }
                return recipientCount;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Group receipt aggregation metadata failed for {canonical}: {ex.Message}");
                return 0;
            }
        }
    }
}
