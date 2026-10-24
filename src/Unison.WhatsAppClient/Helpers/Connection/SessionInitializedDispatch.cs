// =============================================================================
// SessionInitializedDispatch
//
// OnSessionInitialized body: pairing flags, persist, defer post-replay vs
// enrichment. Handler wiring stays on the host.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;

namespace Unison.WhatsAppClient.Helpers
{
    public static class SessionInitializedDispatch
    {
        public static async Task RunAsync(ISessionInitializedHost host)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (host.FatalSessionEnded)
            {
                host.PairingTrace("OnSessionInitialized IGNORED (fatal session ended)");
                return;
            }

            host.PairingTrace("OnSessionInitialized → raising UI event");
            host.LogSessionInitialized();
            host.MarkSessionEstablished();
            host.ClearPairingRestartPending();
            host.EnablePreSessionCloseAsFatal();
            host.ResetPreSessionCloseStreak();
            host.CompleteSessionEstablishedWait();
            host.EnsureSelfPhonePersisted();
            await host.PersistAuthStateForSessionAsync().ConfigureAwait(false);

            if (host.ShouldDeferReconnectReplayWork())
            {
                host.LogDeferringReconnectWork();
            }
            else
            {
                host.SchedulePostReplayMaintenance();
                host.StartMessageStoreForceHistoryRepair("session-initialized");
            }

            host.RaiseSessionInitialized();
            host.PairingTrace("OnSessionInitialized UI event raised");
        }
    }
}
