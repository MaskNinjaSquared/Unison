// =============================================================================
// IOutgoingReceiptHost
//
// Timeline / pending-status / group-count half of receipt handling. MessageFacade
// owns the receipt read + group tally; the client still mutates MessagesByChat.
// =============================================================================
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IOutgoingReceiptHost
    {
        Task ApplyOutgoingStatusAsync(string messageId, string status, string error = null);

        bool IsSelfLinked(string jid);

        /// <summary>
        /// How many peers a group send must reach before the tick fills in (cached metadata).
        /// </summary>
        Task<int> GetExpectedGroupRecipientCountAsync(string groupJid);
    }
}
