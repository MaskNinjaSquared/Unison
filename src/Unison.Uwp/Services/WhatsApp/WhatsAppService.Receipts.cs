using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.Uwp.Services.WhatsApp
{
    public partial class WhatsAppService : IOutgoingReceiptHost
    {
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
                IGroupService groups = Groups;
                if (groups != null)
                {
                    _ = groups.ApplyGroupMetadataFromResponseAsync(canonical, response);
                }
                else
                {
                    Debug.WriteLine("[WhatsAppService] Group metadata apply skipped: IGroupService missing");
                }

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
    }
}
