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

            host.NoteConnectionStateInitStep("message-store");
            await host.InitializeMessageStoreAsync().ConfigureAwait(false);
            host.NoteConnectionStateInitStep("history-freshness");
            host.LoadHistoryFreshnessRepairState();
            host.NoteConnectionStateInitStep("incoming-journal");
            await host.RecoverPendingIncomingJournalAsync().ConfigureAwait(false);
            host.NoteConnectionStateInitStep("auth-load");
            await host.LoadOrCreateAuthStateAsync().ConfigureAwait(false);
            host.NoteConnectionStateInitStep("linked-account-gate");
            host.ApplyLinkedAccountReconnectToastGate();
            host.NoteConnectionStateInitStep("lid-warm");
            await host.WarmLidMappingCacheAsync().ConfigureAwait(false);
            host.NoteConnectionStateInitStep("jid-aliases");
            await host.RestorePersistedJidAliasesAsync().ConfigureAwait(false);
            host.NoteConnectionStateInitStep("self-alias");
            host.RegisterSelfIdentitySocketAlias();
            host.NoteConnectionStateInitStep("self-profile");
            host.SyncSelfProfileFromAuth();
            host.NoteConnectionStateInitStep("done");
        }
    }
}
