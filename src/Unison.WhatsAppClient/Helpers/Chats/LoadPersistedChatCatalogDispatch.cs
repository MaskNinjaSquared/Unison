// =============================================================================
// LoadPersistedChatCatalogDispatch
//
// LoadPersistedChatsAsync orchestration. Preview store I/O and UI list apply
// stay on the host.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Helpers
{
    public static class LoadPersistedChatCatalogDispatch
    {
        public static async Task RunAsync(ILoadPersistedChatCatalogHost host)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            using (host.BeginPersistedChatsLoad())
            {
                try
                {
                    IReadOnlyList<HistoryChatPreview> stored =
                        await host.ReadAllPreviewsAsync().ConfigureAwait(false);
                    if (stored == null || stored.Count == 0)
                    {
                        return;
                    }

                    await host.ApplyPreviewsToChatListAsync(stored).ConfigureAwait(false);
                    host.LogLoaded(stored.Count);
                    host.NotifyHistorySyncAfterLoad();
                }
                catch (Exception ex)
                {
                    host.LogLoadFailed(ex);
                }
            }
        }
    }
}
