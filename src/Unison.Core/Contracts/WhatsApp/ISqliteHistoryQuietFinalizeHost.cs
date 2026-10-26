// =============================================================================
// ISqliteHistoryQuietFinalizeHost
//
// Side effects for SqliteHistoryQuietFinalizeDispatch after a non-on-demand
// SQLite history quiet timer. Delay / generation live on SqliteHistoryQuietFinalize.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.State;

namespace Unison.Core.Contracts.WhatsApp
{
    public interface ISqliteHistoryQuietFinalizeHost
    {
        int SqliteHistoryConversationsAccumulated { get; set; }

        bool FirstSyncListReleaseDeferred { get; set; }

        bool IsHistoryCatchUpActive { get; }

        bool IsHistoryCatchUpBannerActive { get; }

        bool EnrichmentAwaitingHeavyHistory { get; }

        bool IsFirstSyncListHoldInProgress { get; }

        bool IsListPaintPending { get; }

        bool TryHandOffQuietLot(
            bool catchUpBanner,
            out string lotRequestId,
            out HistoryOnDemandRequest lotRequest);

        void PublishInitialSyncProgress(
            bool isActive,
            bool isCompleted,
            int processed,
            int total,
            string reason);

        void RequestFirstSyncListReleaseAfterNames(string reason, int processed, int total);

        void NotifyCatchUpIdleBatch(
            string lotRequestId,
            bool lotAckAccepted,
            int lotProgressSignals,
            string triggerDetail,
            int delayMs);

        void FinishHistoryCatchUpContinue(string reason, bool releaseEnrichment);

        void ClearFullHistoryOnDemandRequestState(string reason);

        void EndHistoryCatchUpBanner(string reason);

        void WriteCatchUpSqliteLotQuiet(
            string lotRequestId,
            int lotProgressSignals,
            int delayMs,
            int processed,
            bool deferListRelease);

        void WriteCatchUpSqliteLotQuietSkipEmpty(string lotRequestId, bool firstSyncHold);

        void WriteCatchUpSqliteLotQuietKeepHold(string lotRequestId);

        void WritePreviewReconcileDeferredListPaint();

        void WritePreviewReconcileDeferredCatchUp();

        Task ReconcileChatPreviewsFromSqliteAsync();

        void LogPreviewReconcileFailed(Exception ex);

        void LogFinalizeFailed(Exception ex);

        void LogQuietLotHandOff(
            string lotRequestId,
            int lotProgressSignals,
            int processed,
            bool bannerActive,
            bool deferListRelease);
    }
}
