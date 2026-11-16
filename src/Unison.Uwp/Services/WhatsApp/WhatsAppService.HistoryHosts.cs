using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;

namespace Unison.Uwp.Services.WhatsApp
{
    public partial class WhatsAppService
    {
// --- IHistoryHydrationBackfillHost ------------------------------------

        bool IHistoryHydrationBackfillHost.IsConnected => IsConnected;

        int IHistoryHydrationBackfillHost.HydrationBatchSize => HistoryHydrationBatchSize;

        int IHistoryHydrationBackfillHost.HydrationPageSize => HistoryHydrationPageSize;

        int IHistoryHydrationBackfillHost.HydrationMaxPagesPerChat =>
            HistoryHydrationMaxPagesPerChat;

        IReadOnlyList<HistoryBackfillCandidate> IHistoryHydrationBackfillHost.BuildHydrationCandidates(
            DateTime nowUtc) =>
            BuildHistoryHydrationCandidates(nowUtc);

        int IHistoryHydrationBackfillHost.GetHydrationMessageCount(string jid) =>
            GetHistoryHydrationMessageCount(jid);

        string IHistoryHydrationBackfillHost.NormalizeHydrationJid(string jid) =>
            NormalizeJid(jid);

        Task<bool> IHistoryHydrationBackfillHost.RequestHydrationOnDemandAsync(
            string normJid,
            int pageSize,
            string triggerReason) =>
            RequestHistoryOnDemandIfNeededAsync(normJid, pageSize, triggerReason);

        Task IHistoryHydrationBackfillHost.WaitForHydrationSettleAsync() =>
            _hydrationSettle.WaitAsync();

        void IHistoryHydrationBackfillHost.LogHydrationSkippedNotConnected(string reason) =>
            Debug.WriteLine(
                "[WhatsAppService] History hydration skipped: not connected (" + reason + ")");

        void IHistoryHydrationBackfillHost.LogHydrationNoCandidates(string reason) =>
            Debug.WriteLine(
                "[WhatsAppService] History hydration: no stale chats with cursor (" + reason + ")");

        void IHistoryHydrationBackfillHost.WriteHydrationStart(
            string reason,
            int chatCount,
            int batchSize,
            int maxPages)
        {
            Debug.WriteLine(
                "[WhatsAppService] History hydration starting: chats=" + chatCount +
                " batchSize=" + batchSize +
                " maxPages=" + maxPages +
                " reason=" + reason);
            _diagnostics.Write(
                "history",
                "hydration-backfill-start",
                "reason=" + (reason ?? string.Empty) +
                "; chats=" + chatCount +
                "; batchSize=" + batchSize +
                "; maxPages=" + maxPages);
        }

        void IHistoryHydrationBackfillHost.WriteHydrationBatch(
            int batchIndex,
            int sentCount,
            int ramGrew,
            int hasMoreCount,
            HistoryBackfillContinueAction action) =>
                    _diagnostics.Write(
                        "history",
                        "hydration-backfill-batch",
                        "batch=" + batchIndex +
                "; sent=" + sentCount +
                "; ramGrew=" + ramGrew +
                "; hasMore=" + hasMoreCount +
                "; action=" + action);

        void IHistoryHydrationBackfillHost.LogHydrationDone(
            int requested,
            int synced,
            int hasMoreLeft,
            int failed) =>
                Debug.WriteLine(
                    "[WhatsAppService] History hydration done: requested=" + requested +
                " synced=" + synced +
                " hasMoreLeft=" + hasMoreLeft +
                " failed=" + failed);

        // --- ISqliteHistoryQuietFinalizeHost ---------------------------------

        int ISqliteHistoryQuietFinalizeHost.SqliteHistoryConversationsAccumulated
        {
            get => SqliteHistoryConversationsAccumulated;
            set => SqliteHistoryConversationsAccumulated = value;
        }

        bool ISqliteHistoryQuietFinalizeHost.FirstSyncListReleaseDeferred
        {
            get => _firstSyncListReleaseDeferred;
            set => _firstSyncListReleaseDeferred = value;
        }

        bool ISqliteHistoryQuietFinalizeHost.IsHistoryCatchUpActive =>
            IsHistoryCatchUpActive();

        bool ISqliteHistoryQuietFinalizeHost.IsHistoryCatchUpBannerActive =>
            IsHistoryCatchUpBannerActive;

        bool ISqliteHistoryQuietFinalizeHost.EnrichmentAwaitingHeavyHistory =>
            _heavyHistoryEnrichment.IsAwaitingHeavyHistory;

        bool ISqliteHistoryQuietFinalizeHost.IsFirstSyncListHoldInProgress =>
            IsFirstSyncListHoldInProgress;

        bool ISqliteHistoryQuietFinalizeHost.IsListPaintPending =>
            _listPaintGate != null && !_listPaintGate.IsReleased;

        bool ISqliteHistoryQuietFinalizeHost.TryHandOffQuietLot(
            bool catchUpBanner,
            out string lotRequestId,
            out HistoryOnDemandRequest lotRequest) =>
            _historyRequests.TryHandOffQuietLot(catchUpBanner, out lotRequestId, out lotRequest);

        void ISqliteHistoryQuietFinalizeHost.PublishInitialSyncProgress(
            bool isActive,
            bool isCompleted,
            int processed,
            int total,
            string reason) =>
            PublishInitialSyncProgress(isActive, isCompleted, processed, total, reason);

        void ISqliteHistoryQuietFinalizeHost.RequestFirstSyncListReleaseAfterNames(
            string reason,
            int processed,
            int total) =>
            RequestFirstSyncListReleaseAfterNames(reason, processed, total);

        void ISqliteHistoryQuietFinalizeHost.NotifySilentFullLotQuiet(
            string triggerReason,
            int appliedCount) =>
            _silentChain.OnLotQuiet(triggerReason, appliedCount);

        void ISqliteHistoryQuietFinalizeHost.NotifyCatchUpIdleBatch(
            string lotRequestId,
            bool lotAckAccepted,
            int lotProgressSignals,
            string triggerDetail,
            int delayMs) =>
            History?.NotifyCatchUpIdleBatch(
                lotRequestId,
                lotAckAccepted,
                lotProgressSignals,
                triggerDetail,
                delayMs);

        void ISqliteHistoryQuietFinalizeHost.FinishHistoryCatchUpContinue(
            string reason,
            bool releaseEnrichment) =>
            _catchUpFinish.Run(reason, releaseEnrichment);

        void ISqliteHistoryQuietFinalizeHost.ClearFullHistoryOnDemandRequestState(string reason) =>
            _fullHistoryClear.Run(reason);

        void ISqliteHistoryQuietFinalizeHost.EndHistoryCatchUpBanner(string reason) =>
            EndHistoryCatchUpBanner(reason);

        void ISqliteHistoryQuietFinalizeHost.WriteCatchUpSqliteLotQuiet(
            string lotRequestId,
            int lotProgressSignals,
            int delayMs,
            int processed,
            bool deferListRelease) =>
            _diagnostics.Write(
                "history",
                "catch-up-sqlite-lot-quiet",
                "requestId=" + (lotRequestId ?? string.Empty) +
                "; signals=" + lotProgressSignals +
                "; delayMs=" + delayMs +
                "; processed=" + processed +
                "; deferListRelease=" + deferListRelease);

        void ISqliteHistoryQuietFinalizeHost.WriteCatchUpSqliteLotQuietSkipEmpty(
            string lotRequestId,
            bool firstSyncHold) =>
            _diagnostics.Write(
                "history",
                "catch-up-sqlite-lot-quiet-skip-empty",
                "requestId=" + (lotRequestId ?? string.Empty) +
                "; firstSyncHold=" + firstSyncHold);

        void ISqliteHistoryQuietFinalizeHost.WriteCatchUpSqliteLotQuietKeepHold(
            string lotRequestId) =>
            _diagnostics.Write(
                "history",
                "catch-up-sqlite-lot-quiet-keep-hold",
                "requestId=" + (lotRequestId ?? string.Empty) +
                "; reason=first-sync-awaiting-bootstrap");

        void ISqliteHistoryQuietFinalizeHost.WritePreviewReconcileDeferredListPaint() =>
            _diagnostics.Write(
                "preview-tip",
                "reconcile-deferred-list-paint",
                "reason=history-quiet");

        void ISqliteHistoryQuietFinalizeHost.WritePreviewReconcileDeferredCatchUp() =>
            _diagnostics.Write(
                "preview-tip",
                "reconcile-deferred-catch-up",
                "reason=history-quiet");

        async Task ISqliteHistoryQuietFinalizeHost.ReconcileChatPreviewsFromSqliteAsync()
        {
            if (ChatFacade != null)
            {
                await ChatFacade.ReconcileChatPreviewsFromSqliteAsync(null, "history-quiet")
                    .ConfigureAwait(false);
            }
        }

        void ISqliteHistoryQuietFinalizeHost.LogPreviewReconcileFailed(Exception ex) =>
            Debug.WriteLine(
                "[WhatsAppService] Post-sync preview reconcile failed: " +
                (ex != null ? ex.Message : string.Empty));

        void ISqliteHistoryQuietFinalizeHost.LogFinalizeFailed(Exception ex) =>
            Debug.WriteLine(
                "[WhatsAppService] FinalizeSqliteHistoryProgress failed: " +
                (ex != null ? ex.Message : string.Empty));

        void ISqliteHistoryQuietFinalizeHost.LogQuietLotHandOff(
            string lotRequestId,
            int lotProgressSignals,
            int processed,
            bool bannerActive,
            bool deferListRelease) =>
            Debug.WriteLine(
                "[WhatsAppService] SQLite quiet lot handed to catch-up continue: requestId=" +
                (lotRequestId ?? "<none>") +
                ", signals=" + lotProgressSignals +
                ", processed=" + processed +
                ", banner=" + bannerActive +
                ", deferListRelease=" + deferListRelease);

        private IReadOnlyList<HistoryBackfillCandidate> BuildHistoryHydrationCandidates(
            DateTime nowUtc) =>
            _hydrationCandidates.Build(nowUtc);

        private int GetHistoryHydrationMessageCount(string jid) =>
            string.IsNullOrWhiteSpace(jid) ? 0 : _chatState.GetMessageCount(jid);

        /// <summary>
        /// Rebuilds a message we sent, so a retry receipt that arrives after the socket's cache
        /// has expired can still be answered.
        ///
        /// Only text is rebuilt. A media message is stored as its download keys rather than as
        /// the stanza that carried it, and a reconstruction would differ from what the peer is
        /// asking to see again - so it declines instead, and the peer falls back to asking our
        /// phone. Answering with the wrong bytes is worse than not answering.
        /// </summary>
        internal Task<global::Proto.Message> ResolveSentMessageForRetryAsync(
            string remoteJid,
            string messageId) =>
            _persistedLookup.ResolveSentForRetryAsync(remoteJid, messageId);

        /// <summary>
        /// Stops the reconnect loop before the close handler or a ping waiter can schedule
        /// another attempt. 401 arrives as a &lt;failure&gt; after Noise, FailPendingWaiters
        /// unblocks the health probe, and the loop would otherwise already be in its backoff
        /// before the facade runs.
        /// </summary>

    }
}
