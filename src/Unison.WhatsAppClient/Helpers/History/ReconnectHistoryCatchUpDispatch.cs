// =============================================================================
// ReconnectHistoryCatchUpDispatch
//
// Body of RequestReconnectHistoryCatchUpAsync. Quiet wait / FULL request I/O
// stay on the host.
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class ReconnectHistoryCatchUpDispatch
    {
        public static async Task RunAsync(IReconnectHistoryCatchUpHost host, string reason)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            try
            {
                bool catchUpActive = host.IsHistoryCatchUpActive;
                bool quiet = await host.WaitForStartupQuietAsync(
                        TimeSpan.FromSeconds(1.5),
                        "full-history-catchup-floor",
                        CancellationToken.None)
                    .ConfigureAwait(false);

                ReconnectHistoryCatchUpFloorAction floor =
                    ReconnectHistoryCatchUpFloorGates.Decide(
                        quiet,
                        host.IsConnected,
                        catchUpActive,
                        host.IsWindowVisible);
                if (floor != ReconnectHistoryCatchUpFloorAction.Proceed)
                {
                    host.LogReconnectHistoryCatchUp(
                        "FULL_HISTORY catch-up floor aborted (" +
                        reason + "); catchUpActive=" + catchUpActive +
                        "; releasing enrichment=" +
                        (floor == ReconnectHistoryCatchUpFloorAction.AbortAndReleaseEnrichment));
                    if (floor == ReconnectHistoryCatchUpFloorAction.AbortAndReleaseEnrichment)
                    {
                        host.ClearEnrichmentAwaitingHeavyHistory();
                        host.TrySchedulePostMessageEnrichment("catchup-floor-aborted:" + reason);
                    }

                    return;
                }

                host.ReleaseFullHistoryIfUntracked();

                bool ok = await host.RequestFullHistoryOnDemandTrackedAsync(
                        reason,
                        isFreshnessRepair: true)
                    .ConfigureAwait(false);
                if (ok)
                {
                    host.LogReconnectHistoryCatchUp(
                        "FULL_HISTORY requested for reconnect catch-up (" + reason + ")");
                    host.WriteReconnectHistoryCatchUpJournal(reason);
                    return;
                }

                host.LogReconnectHistoryCatchUp(
                    "FULL_HISTORY not sent for reconnect catch-up (" +
                    reason + "); releasing enrichment so the session is not stuck silent.");
                host.ClearEnrichmentAwaitingHeavyHistory();
                host.TrySchedulePostMessageEnrichment("catchup-failed:" + reason);
            }
            catch (Exception ex)
            {
                host.LogReconnectHistoryCatchUp(
                    "Reconnect history catch-up failed (" + reason + "): " + ex.Message);
                host.ClearEnrichmentAwaitingHeavyHistory();
                host.TrySchedulePostMessageEnrichment("catchup-error:" + reason);
            }
        }
    }
}
