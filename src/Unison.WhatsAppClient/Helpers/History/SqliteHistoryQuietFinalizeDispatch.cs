// =============================================================================
// SqliteHistoryQuietFinalizeDispatch
//
// Body of FinalizeSqliteHistoryProgressAfterQuietAsync after the quiet delay
// and generation check. Timer / generation CAS stay on the host entry.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.State;

namespace Unison.WhatsAppClient.Helpers
{
    public static class SqliteHistoryQuietFinalizeDispatch
    {
        public static async Task RunAsync(
            ISqliteHistoryQuietFinalizeHost host,
            int delayMs,
            int processedAtSchedule)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            try
            {
                int processed = Math.Max(
                    processedAtSchedule,
                    host.SqliteHistoryConversationsAccumulated);
                int total = Math.Max(processed, 1);

                bool catchUpBanner = host.IsHistoryCatchUpActive;
                bool handOffCatchUp = host.TryHandOffQuietLot(
                    catchUpBanner,
                    out string lotRequestId,
                    out HistoryOnDemandRequest lotRequest);
                bool enrichmentAwaiting = host.EnrichmentAwaitingHeavyHistory;
                bool firstSyncInProgress = host.IsFirstSyncListHoldInProgress;
                bool deferListRelease = HistoryFirstSyncListHoldDecision.ShouldDeferInitialSyncCompleted(
                    firstSyncInProgress,
                    handOffCatchUp,
                    catchUpBanner,
                    enrichmentAwaiting);

                if (deferListRelease)
                {
                    host.PublishInitialSyncProgress(
                        true,
                        false,
                        processed,
                        total,
                        "sqlite-quiet-catch-up");
                    host.FirstSyncListReleaseDeferred = true;
                }
                else if (HistoryFirstSyncListHoldDecision.ShouldResolveNamesBeforeListRelease(
                    firstSyncInProgress))
                {
                    host.RequestFirstSyncListReleaseAfterNames(
                        "sqlite-finalized",
                        processed,
                        total);
                }
                else
                {
                    host.PublishInitialSyncProgress(
                        false,
                        true,
                        processed,
                        total,
                        "sqlite-finalized");
                    host.SqliteHistoryConversationsAccumulated = 0;
                }

                bool lotAckAccepted = lotRequest != null && lotRequest.AckAccepted;
                int lotProgressSignals = lotRequest != null ? lotRequest.ProgressSignalCount : 0;
                string lotTrigger = lotRequest != null
                    ? (lotRequest.TriggerReason ?? "sqlite-lot-quiet")
                    : "sqlite-lot-quiet";

                if (handOffCatchUp)
                {
                    host.LogQuietLotHandOff(
                        lotRequestId,
                        lotProgressSignals,
                        processed,
                        host.IsHistoryCatchUpBannerActive,
                        deferListRelease);
                    host.WriteCatchUpSqliteLotQuiet(
                        lotRequestId,
                        lotProgressSignals,
                        delayMs,
                        processed,
                        deferListRelease);

                    if (processed <= 0)
                    {
                        host.WriteCatchUpSqliteLotQuietSkipEmpty(
                            lotRequestId,
                            firstSyncInProgress);
                        if (HistoryFirstSyncListHoldDecision.ShouldFinishCatchUpOnEmptyQuietLot(
                            firstSyncInProgress,
                            processed))
                        {
                            host.FinishHistoryCatchUpContinue(
                                "sqlite-empty-lot",
                                releaseEnrichment: true);
                        }
                        else
                        {
                            host.WriteCatchUpSqliteLotQuietKeepHold(lotRequestId);
                        }
                    }
                    else
                    {
                        host.NotifyCatchUpIdleBatch(
                            lotRequestId,
                            lotAckAccepted,
                            lotProgressSignals,
                            "sqlite-lot-quiet:" + (lotTrigger ?? string.Empty),
                            delayMs);
                    }
                }
                else
                {
                    host.ClearFullHistoryOnDemandRequestState("sqlite-finalized");
                    host.EndHistoryCatchUpBanner("sqlite-finalized");
                }

                if (host.IsListPaintPending)
                {
                    host.WritePreviewReconcileDeferredListPaint();
                }
                else if (HistoryCatchUpLocalWorkPolicy.ShouldDeferHeavyLocalWork(
                    host.IsHistoryCatchUpActive))
                {
                    host.WritePreviewReconcileDeferredCatchUp();
                }
                else
                {
                    try
                    {
                        await host.ReconcileChatPreviewsFromSqliteAsync()
                            .ConfigureAwait(false);
                    }
                    catch (Exception exReconcile)
                    {
                        host.LogPreviewReconcileFailed(exReconcile);
                    }
                }
            }
            catch (Exception ex)
            {
                host.LogFinalizeFailed(ex);
            }
        }
    }
}
