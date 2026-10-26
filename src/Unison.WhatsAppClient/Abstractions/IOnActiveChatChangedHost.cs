// =============================================================================
// IOnActiveChatChangedHost
//
// Side effects for OnActiveChatChangedDispatch.
// =============================================================================

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IOnActiveChatChangedHost
    {
        void UnloadMessageCacheIfInactive(string jid);

        void ClearSyncStatus();
    }
}
