// =============================================================================
// UnloadMessageCacheIfInactiveDispatch
//
// Body of UnloadMessageCacheIfInactive. Cache dictionary stays on the host.
// =============================================================================
using System;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class UnloadMessageCacheIfInactiveDispatch
    {
        public static void Run(IUnloadMessageCacheIfInactiveHost host, string jid)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (string.IsNullOrWhiteSpace(jid) || host.IsActiveChatJid(jid))
            {
                return;
            }

            string normalized = host.NormalizeJid(jid);
            host.RemoveMessagesByChatKey(normalized);
            if (!string.Equals(normalized, jid, StringComparison.OrdinalIgnoreCase))
            {
                host.RemoveMessagesByChatKey(jid);
            }
        }
    }
}
