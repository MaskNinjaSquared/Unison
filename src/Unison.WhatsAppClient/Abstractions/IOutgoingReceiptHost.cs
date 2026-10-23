// =============================================================================
// IOutgoingReceiptHost
//
// Group-count half of receipt handling. MessageFacade owns receipt read, group
// tally, metadata apply, and ApplyOutgoingStatusAsync; the client answers the
// wire IQ and the recipient-count cache.
// =============================================================================
using System.Threading.Tasks;
using Unison.Baileys.Protocol;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IOutgoingReceiptHost
    {
        bool IsSelfLinked(string jid);

        /// <summary>Cached peer count when still fresh; otherwise false.</summary>
        bool TryGetFreshGroupRecipientCount(string groupJid, out int recipientCount);

        /// <summary>Remembers a counted peer total after a fresh metadata IQ.</summary>
        void RememberGroupRecipientCount(string groupJid, int recipientCount);

        /// <summary>Wire group metadata IQ. Null when the socket is gone.</summary>
        Task<BinaryNode> QueryGroupMetadataAsync(string groupJid);
    }
}
