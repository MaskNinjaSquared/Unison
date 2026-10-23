using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Messages;

namespace Unison.Uwp.Services.WhatsApp
{
    public partial class WhatsAppService : IOutgoingReceiptHost
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
            if (Messages != null)
            {
                await Messages.HandleReceiptAsync(node).ConfigureAwait(false);
            }
        }

        bool IOutgoingReceiptHost.IsSelfLinked(string jid) => IsSelfLinkedJid(jid);

        Task<int> IOutgoingReceiptHost.GetExpectedGroupRecipientCountAsync(string groupJid) =>
            GetExpectedGroupRecipientCountAsync(groupJid);

        private async Task<int> GetExpectedGroupRecipientCountAsync(string groupJid)
        {
            string canonical = GetCanonicalJid(groupJid);
            if (string.IsNullOrWhiteSpace(canonical) || _socket == null)
            {
                return 0;
            }

            if (_groupRecipientCounts.TryGetFresh(canonical, DateTime.UtcNow, out int cachedCount))
            {
                return cachedCount;
            }

            try
            {
                var response = await _socket.QueryGroupMetadataAsync(canonical);
                ApplyGroupSendPermissionsFromMetadata(response, canonical);

                int? counted = _receipts.CountRecipients(response);
                if (counted == null)
                {
                    return 0;
                }

                int recipientCount = counted.Value;
                _groupRecipientCounts.Store(canonical, recipientCount, DateTime.UtcNow);
                return recipientCount;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[WhatsAppService] Group receipt aggregation metadata failed for {canonical}: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Receipt path: apply a metadata IQ the client already fetched (no second query).
        /// </summary>
        private void ApplyGroupSendPermissionsFromMetadata(BinaryNode response, string groupJid)
        {
            GroupMetadataSnapshot snapshot = _groupMetadata.ReadMetadata(response, groupJid);
            IGroupService groups = Groups;
            if (groups == null)
            {
                Debug.WriteLine("[WhatsAppService] Group metadata apply skipped: IGroupService missing");
                return;
            }

            _ = groups.ApplyGroupMetadataAsync(groupJid, snapshot);
        }
    }
}
