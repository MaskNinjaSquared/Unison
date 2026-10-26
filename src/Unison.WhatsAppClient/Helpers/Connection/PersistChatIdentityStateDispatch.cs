// =============================================================================
// PersistChatIdentityStateDispatch
//
// Body of PersistChatIdentityStateAsync. UI capture / store writes stay on the
// host.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class PersistChatIdentityStateDispatch
    {
        public static async Task RunAsync(IPersistChatIdentityStateHost host, string reason)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            try
            {
                ChatIdentityCapture capture = await host.CaptureChatIdentitySnapshotsAsync()
                    .ConfigureAwait(false);
                ChatCatalogSnapshot catalog = capture != null ? capture.Catalog : null;
                if (catalog == null || catalog.ChatJids == null || catalog.ChatJids.Count == 0)
                {
                    return;
                }

                IReadOnlyDictionary<string, string> contacts =
                    capture.ContactNames ?? new Dictionary<string, string>();
                IReadOnlyDictionary<string, string> phones =
                    capture.PhoneNames ?? new Dictionary<string, string>();
                IReadOnlyDictionary<string, string> aliases =
                    capture.Aliases ?? new Dictionary<string, string>();

                await host.PersistChatCatalogAsync(catalog).ConfigureAwait(false);
                await host.SaveContactNamesAsync(contacts, catalog.ChatJids).ConfigureAwait(false);
                await host.SavePhoneContactNamesAsync(phones, catalog.ChatJids).ConfigureAwait(false);
                await host.SaveJidAliasesAsync(aliases, catalog.ChatJids).ConfigureAwait(false);
                host.LogPersistChatIdentity(
                    "Persisted chat identity state immediately (" + reason + ")");
            }
            catch (Exception ex)
            {
                host.LogPersistChatIdentity(
                    "Failed to persist chat identity state immediately (" + reason + "): " +
                    ex.Message);
            }
        }
    }
}
