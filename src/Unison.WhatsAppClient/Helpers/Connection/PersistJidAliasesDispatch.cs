// =============================================================================
// PersistJidAliasesDispatch
//
// Body of PersistJidAliasesAsync. UI chat list / store stay on the host.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class PersistJidAliasesDispatch
    {
        public static async Task RunAsync(IPersistJidAliasesHost host, string reason)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            try
            {
                IReadOnlyList<string> chatJids = await host.CollectDistinctChatJidsAsync()
                    .ConfigureAwait(false);
                if (chatJids == null || chatJids.Count == 0)
                {
                    return;
                }

                IReadOnlyDictionary<string, string> aliasSnapshot = host.SnapshotJidAliases();
                await host.SaveJidAliasesAsync(aliasSnapshot, chatJids).ConfigureAwait(false);
                host.LogPersistedJidAliases(aliasSnapshot.Count, reason);
            }
            catch (Exception ex)
            {
                host.LogPersistJidAliasesFailed(reason, ex.Message);
            }
        }
    }
}
