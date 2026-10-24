// =============================================================================
// OnActiveChatChangedDispatch
//
// Body of OnActiveChatChanged. Cache unload / status stay on the host.
// =============================================================================
using System;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class OnActiveChatChangedDispatch
    {
        public static void Run(
            IOnActiveChatChangedHost host,
            string previous,
            string next)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (!string.IsNullOrWhiteSpace(previous) &&
                !string.Equals(previous, next, StringComparison.OrdinalIgnoreCase))
            {
                host.UnloadMessageCacheIfInactive(previous);
            }

            // Mobile StatusBar is global: clear list-startup banners when the user opens a chat.
            if (!string.IsNullOrWhiteSpace(next))
            {
                host.ClearSyncStatus();
            }
        }
    }
}
