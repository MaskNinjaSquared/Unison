using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Core.Contracts.WhatsApp;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.Uwp.Services.WhatsApp
{
    public partial class WhatsAppService : IOutgoingReceiptHost
    {
        bool IOutgoingReceiptHost.IsSelfLinked(string jid) => IsSelfLinkedJid(jid);

        bool IOutgoingReceiptHost.TryGetFreshGroupRecipientCount(string groupJid, out int recipientCount)
        {
            string canonical = GetCanonicalJid(groupJid);
            if (string.IsNullOrWhiteSpace(canonical))
            {
                recipientCount = 0;
                return false;
            }

            return _groupRecipientCounts.TryGetFresh(canonical, DateTime.UtcNow, out recipientCount);
        }

        void IOutgoingReceiptHost.RememberGroupRecipientCount(string groupJid, int recipientCount)
        {
            string canonical = GetCanonicalJid(groupJid);
            if (string.IsNullOrWhiteSpace(canonical) || recipientCount <= 0)
            {
                return;
            }

            _groupRecipientCounts.Store(canonical, recipientCount, DateTime.UtcNow);
        }

        async Task<BinaryNode> IOutgoingReceiptHost.QueryGroupMetadataAsync(string groupJid)
        {
            string canonical = GetCanonicalJid(groupJid);
            if (string.IsNullOrWhiteSpace(canonical) || _socket == null)
            {
                return null;
            }

            try
            {
                return await _socket.QueryGroupMetadataAsync(canonical).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    "[WhatsAppService] Group metadata query failed for " +
                    canonical + ": " + ex.Message);
                return null;
            }
        }
    }
}
