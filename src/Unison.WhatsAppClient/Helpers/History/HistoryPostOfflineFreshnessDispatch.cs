// =============================================================================
// HistoryPostOfflineFreshnessDispatch
//
// Body of LogHistoryFreshnessAfterOfflineDrain. Tip age / FULL_HISTORY request
// I/O stay on the host.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class HistoryPostOfflineFreshnessDispatch
    {
        public static async Task RunAsync(
            IHistoryPostOfflineFreshnessHost host,
            int offlineCount)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            DateTime nowUtc = DateTime.UtcNow;

            if (host.LastHistorySyncReceivedUtc != DateTime.MinValue)
            {
                host.LogSyncObservedThisSession(
                    host.LastHistorySyncReceivedUtc,
                    host.LastHistorySyncTypeOrUnknown);
            }

            bool catchUpActive = host.IsHistoryCatchUpActive;

            if (catchUpActive && offlineCount > 0)
            {
                host.NoteCatchUpProgress("offline-drain:" + offlineCount);
            }

            string staleReason;
            if (!host.TryGetFreshnessStaleReason(nowUtc, out staleReason))
            {
                host.LogFreshnessPassed(
                    FreshnessTimestampFormat.ForLog(host.GetNewestStoredMessageUtc()),
                    FreshnessTimestampFormat.ForLog(host.GetNewestNonSelfStoredMessageUtc()),
                    FreshnessTimestampFormat.ForLog(host.GetNewestGroupStoredMessageUtc()),
                    offlineCount,
                    catchUpActive);

                if (catchUpActive)
                {
                    host.MarkEnrichmentAwaitingHeavyHistory();

                    int stagnantStop = host.CatchUpStagnantCyclesBeforeStop;
                    HistoryCatchUpDrainAction drain = host.DecideAfterFreshDrain(
                        offlineCount,
                        stagnantStop,
                        out int stagnantCycles);
                    if (drain == HistoryCatchUpDrainAction.Continue)
                    {
                        await host.RequestReconnectHistoryCatchUpAsync(
                                "catch-up-continue-offline:" + offlineCount)
                            .ConfigureAwait(false);
                        return;
                    }

                    host.LogCatchUpEmptyWhileTipFresh(stagnantCycles, stagnantStop);
                    if (drain == HistoryCatchUpDrainAction.Finish)
                    {
                        host.FinishHistoryCatchUpContinue(
                            "offline-fresh-empty",
                            releaseEnrichment: true);
                        return;
                    }

                    await host.RequestReconnectHistoryCatchUpAsync(
                            "catch-up-retry-empty:" + stagnantCycles)
                        .ConfigureAwait(false);
                    return;
                }

                host.FinishHistoryCatchUpContinue("offline-fresh", releaseEnrichment: true);
                return;
            }

            DateTime newestStoredMessageUtc = host.GetNewestStoredMessageUtc();
            string newestText = FreshnessTimestampFormat.ForLog(newestStoredMessageUtc);
            TimeSpan? tipAge = newestStoredMessageUtc == DateTime.MinValue
                ? (TimeSpan?)null
                : nowUtc - newestStoredMessageUtc;

            if (!HistoryPostOfflineCatchUpDecision.ShouldRequestFullHistory(
                    offlineCount,
                    stillStale: true,
                    staleReason,
                    tipAge))
            {
                host.LogFreshnessNotStaleSkip(
                    staleReason,
                    newestText,
                    tipAge.HasValue ? tipAge.Value.TotalMinutes.ToString("F1") : "<none>",
                    offlineCount,
                    catchUpActive);
                host.FinishHistoryCatchUpContinue(
                    "offline-empty-age-stale",
                    releaseEnrichment: true);
                return;
            }

            host.LogFreshnessStillStaleRequest(staleReason, newestText, offlineCount);
            host.MarkEnrichmentAwaitingHeavyHistory();
            await host.RequestReconnectHistoryCatchUpAsync(
                    "offline-stale:" + offlineCount + ":" + staleReason)
                .ConfigureAwait(false);
        }
    }
}
