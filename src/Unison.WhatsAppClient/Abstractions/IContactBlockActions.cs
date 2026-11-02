// =============================================================================
// IContactBlockActions
//
// Baileys updateBlockStatus over the live bridge. ContactFacade confirms and
// calls this; no blocklist cache in this slice.
// =============================================================================
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IContactBlockActions
    {
        bool CanBlock { get; }

        Task BlockAsync(string jid);
    }
}
