// =============================================================================
// LoadPersistedUiStateDispatch
//
// Body of LoadPersistedUiStateAsync. Disk / façade I/O stay on the host.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class LoadPersistedUiStateDispatch
    {
        public static async Task RunAsync(ILoadPersistedUiStateHost host)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            await host.InitializeConnectionStateAsync().ConfigureAwait(false);
            if (host.PersistedUiStateLoaded || !host.IsAuthRegistered)
            {
                return;
            }

            await host.EnterPersistedUiLoadLockAsync().ConfigureAwait(false);
            try
            {
                if (host.PersistedUiStateLoaded)
                {
                    return;
                }

                await host.LoadPersistedChatsAsync().ConfigureAwait(false);
                host.MarkPersistedUiStateLoaded();
                host.WritePersistedUiLoadedJournal(host.ChatRowCount);

                // Local avatar files are free for the socket; bind them before history catch-up
                // or enrichment so the list shows faces while tips/names settle.
                await host.HydrateCachedAvatarsBestEffortAsync().ConfigureAwait(false);

                // Catalog came from history_chat_preview; fix Last Message from history_message
                // when the preview row is stale (deferred maintenance may have run with empty Chats).
                await host.ReconcileChatPreviewsBestEffortAsync().ConfigureAwait(false);
            }
            finally
            {
                host.ExitPersistedUiLoadLock();
            }
        }
    }
}
