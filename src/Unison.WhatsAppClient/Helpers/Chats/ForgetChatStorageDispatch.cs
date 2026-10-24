// =============================================================================
// ForgetChatStorageDispatch
//
// ForgetChatInStorageAsync body: tombstone previews, drop history, clear RAM
// cache, persist catalogue. Façade entry is IAppStateLocalHost.ForgetChatStorageAsync
// (ForgetChatStorage port; name historical).
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;

namespace Unison.WhatsAppClient.Helpers
{
    public static class ForgetChatStorageDispatch
    {
        public static async Task RunAsync(
            IForgetChatStorageHost host,
            string canonicalJid,
            IReadOnlyList<string> storeKeys)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            IReadOnlyList<string> jids = ForgetChatStorageKeys.Merge(canonicalJid, storeKeys);

            await host.MarkPreviewsDeletedAsync(jids).ConfigureAwait(false);
            await host.DeleteHistoryForKeysAsync(jids).ConfigureAwait(false);
            host.ClearMessageMemoryCache();
            await host.PersistCatalogAfterForgetAsync().ConfigureAwait(false);
        }
    }
}
