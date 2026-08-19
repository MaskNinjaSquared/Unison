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
            if (node?.Attrs == null) return;

            string receiptType = node.Attrs.GetDictionaryValueOrDefault("type", string.Empty);
            if (string.Equals(receiptType, "retry", StringComparison.OrdinalIgnoreCase)) return;

            string status;
            if (string.IsNullOrWhiteSpace(receiptType))
            {
                status = ChatMessage.StatusDelivered;
            }
            else if (string.Equals(receiptType, "sender", StringComparison.OrdinalIgnoreCase))
            {
                status = ChatMessage.StatusSent;
            }
            else if (string.Equals(receiptType, "read", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(receiptType, "read-self", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(receiptType, "played", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(receiptType, "played-self", StringComparison.OrdinalIgnoreCase))
            {
                status = ChatMessage.StatusRead;
            }
            else if (string.Equals(receiptType, "delivery", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(receiptType, "delivered", StringComparison.OrdinalIgnoreCase))
            {
                status = ChatMessage.StatusDelivered;
            }
            else
            {
                // Unknown receipt types must not be promoted to delivered. The official
                // protocol mapping ignores values it does not recognize.
                return;
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (node.Attrs.TryGetValue("id", out var rootId) && !string.IsNullOrWhiteSpace(rootId)) ids.Add(rootId);
            foreach (var item in node.FindAllDescendants("item"))
            {
                if (item?.Attrs != null && item.Attrs.TryGetValue("id", out var itemId) && !string.IsNullOrWhiteSpace(itemId))
                    ids.Add(itemId);
            }

            string receiptChat = NormalizeJid(node.Attrs.GetDictionaryValueOrDefault("from", string.Empty));
            bool isGroupReceipt = !string.IsNullOrWhiteSpace(receiptChat) &&
                receiptChat.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase);

            if (!isGroupReceipt || string.Equals(status, ChatMessage.StatusSent, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var id in ids)
                {
                    await UpdateOutgoingMessageStatusAsync(id, status);
                }
                return;
            }

            string participant = GetCanonicalJid(NormalizeJid(
                node.Attrs.GetDictionaryValueOrDefault("participant", string.Empty)));
            if (string.IsNullOrWhiteSpace(participant) || IsSelfLinkedJid(participant)) return;

            int expectedRecipients = await GetExpectedGroupRecipientCountAsync(receiptChat);
            if (expectedRecipients <= 0) return;

            foreach (var id in ids)
            {
                string aggregateStatus = RegisterGroupReceipt(
                    id,
                    participant,
                    status,
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
                var groupNode = response?.GetChild("group") ?? response?.GetChild("query")?.GetChild("group");
                if (groupNode == null) return 0;

                int recipientCount = groupNode.GetChildren("participant")
                    .Select(participantNode =>
                        participantNode != null && participantNode.Attrs != null
                            ? participantNode.Attrs.GetDictionaryValueOrDefault("jid", string.Empty)
                            : string.Empty)
                    .Where(jid => !string.IsNullOrWhiteSpace(jid))
                    .Select(jid => GetCanonicalJid(NormalizeJid(jid)))
                    .Where(jid => !string.IsNullOrWhiteSpace(jid) && !IsSelfLinkedJid(jid))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();

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
