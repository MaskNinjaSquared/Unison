// =============================================================================
// OfflineReplayRelease
//
// OnReceivedPendingNotifications: flush offline rows, settle façades, schedule
// post-replay maintenance. Handler wiring stays on the host.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;

namespace Unison.WhatsAppClient.Helpers
{
    public static class OfflineReplayRelease
    {
        public static async Task RunAsync(IOfflineReplayReleaseHost host, int offlineCount)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            bool firstRelease = host.TryMarkFirstRelease();
            host.WriteReleaseDiagnostics(offlineCount, firstRelease);
            host.ClearReplayDrainPending();

            await host.FlushOfflineReplayMessagesAsync("offline-complete:" + offlineCount)
                .ConfigureAwait(false);

            bool transportReady = host.IsAssignedTransportReady;
            if (OfflineReplayReleaseGates.ShouldSkipSyncedMark(firstRelease, transportReady))
            {
                host.WriteSyncedSkipped(offlineCount);
            }

            await host.NotifyMessagesReleasedAsync(offlineCount, firstRelease, transportReady)
                .ConfigureAwait(false);

            host.SchedulePostReplayMaintenance(offlineCount);

            if (OfflineReplayReleaseGates.ShouldLogHistoryFreshness(
                    firstRelease,
                    host.IsHistoryCatchUpActive))
            {
                host.LogHistoryFreshnessAfterOfflineDrain(offlineCount);
            }
        }
    }
}
