// =============================================================================
// InitializeConnectionStateDispatch
//
// Body of InitializeConnectionStateAsync inside the init lock. Store / auth /
// alias I/O stay on the host.
// =============================================================================
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class InitializeConnectionStateDispatch
    {
        public static async Task RunAsync(IInitializeConnectionStateHost host)
        {
            if (host == null || host.HasAuthState)
            {
                return;
            }

            await host.InitializeMessageStoreAsync().ConfigureAwait(false);
            host.LoadHistoryFreshnessRepairState();
            await host.RecoverPendingIncomingJournalAsync().ConfigureAwait(false);
            await host.LoadOrCreateAuthStateAsync().ConfigureAwait(false);
            host.ApplyLinkedAccountReconnectToastGate();
            await host.WarmLidMappingCacheAsync().ConfigureAwait(false);
            await host.RestorePersistedJidAliasesAsync().ConfigureAwait(false);
            host.RegisterSelfIdentitySocketAlias();
            host.SyncSelfProfileFromAuth();
        }
    }
}
