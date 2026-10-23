// =============================================================================
// IOutgoingReceiptHost
//
// Group-count half of receipt handling. MessageFacade owns receipt read, group
// tally, and ApplyOutgoingStatusAsync; the client still answers peer counts.
// =============================================================================
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IOutgoingReceiptHost
    {
        bool IsSelfLinked(string jid);

        /// <summary>
        /// How many peers a group send must reach before the tick fills in (cached metadata).
        /// </summary>
        Task<int> GetExpectedGroupRecipientCountAsync(string groupJid);
    }
}
