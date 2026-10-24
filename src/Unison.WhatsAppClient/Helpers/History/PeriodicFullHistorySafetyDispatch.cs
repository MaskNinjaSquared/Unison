// =============================================================================
// PeriodicFullHistorySafetyDispatch
//
// Body of TryPeriodicFullHistorySafetyAsync. Socket / settings / request I/O
// stay on the host.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class PeriodicFullHistorySafetyDispatch
    {
        public static async Task RunAsync(IPeriodicFullHistorySafetyHost host)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            bool tipStale = host.TryGetFreshnessStaleReason(DateTime.UtcNow, out string staleDetail);
            bool forceRepair = host.TryReadForceHistoryRepairPending();

            if (!HistoryPeriodicFullSafetyDecision.ShouldRequest(
                    host.IsSocketReadyForPeriodicFullSafety,
                    host.IsHistoryCatchUpActive,
                    host.IsFullHistoryClaimed,
                    forceRepair,
                    tipStale,
                    out string gateReason))
            {
                return;
            }

            string reason = "periodic-safety:" + gateReason;
            if (tipStale && !string.IsNullOrWhiteSpace(staleDetail))
            {
                reason += ":" + staleDetail;
            }

            host.LogPeriodicFullSafety(
                "Periodic FULL_HISTORY safety soliciting (" + reason + ")");
            host.WritePeriodicFullSafetyJournal(reason);

            bool ok = await host.RequestFullHistoryOnDemandTrackedAsync(
                    reason,
                    isFreshnessRepair: true)
                .ConfigureAwait(false);
            host.LogPeriodicFullSafety(
                "Periodic FULL_HISTORY safety result ok=" + ok + " (" + reason + ")");
        }
    }
}
