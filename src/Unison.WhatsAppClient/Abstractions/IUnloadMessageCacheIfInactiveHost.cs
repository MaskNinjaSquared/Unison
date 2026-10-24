// =============================================================================
// IUnloadMessageCacheIfInactiveHost
//
// Side effects for UnloadMessageCacheIfInactiveDispatch.
// =============================================================================

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IUnloadMessageCacheIfInactiveHost
    {
        bool IsActiveChatJid(string jid);

        string NormalizeJid(string jid);

        void RemoveMessagesByChatKey(string key);
    }
}
