using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Unison.WhatsAppClient.History;

namespace Unison.Uwp.Services.WhatsApp.History
{
    internal sealed partial class HistoryCatchUpHost :
        IHistorySyncHost,
        IHistoryCatchUpHost,
        IHistoryHydrationBackfillHost,
        ISqliteHistoryQuietFinalizeHost,
        IHydrationDayRetryIoHost
    {
        // --- IHistorySyncHost ------------------------------------------------

        public Task<bool> RequestFullHistoryOnDemandAsync(string reason = null, bool force = false)
        {
            if (force)
            {
                _historyRequests.ForceReleaseFull();
            }

            return _requestFullTracked.RunAsync(
                string.IsNullOrWhiteSpace(reason) ? "manual-full-history" : reason,
                isFreshnessRepair: true);
        }

        public void NotifyHistorySqliteChunkApplied(string syncType, int conversationCount) =>
            _historyChunks.OnChunkApplied(syncType, conversationCount);

        /// <summary>
        /// Called when a non-on-demand history chunk starts persisting so the chat list can show
        /// a banner before SQLite returns (previews/messages can take a while on Mobile).
        /// </summary>
        public void NotifyHistorySqliteChunkStarted(string syncType, int conversationCount) =>
            _historyChunks.OnChunkStarted(syncType, conversationCount);

        void IHistorySyncHost.NoteFullHistoryCatchUpProgress(string reason) =>
            NoteFullHistoryCatchUpProgress(reason);

        void IHistorySyncHost.CompleteUserResyncOnFullHistoryAckRejected()
        {
            if (!_userResyncWait.IsWaiting)
            {
                return;
            }

            _catchUpBanner.End("full-history-ack-rejected");
            _userResyncWait.TryFail();
        }

        void IHistorySyncHost.ScheduleAcceptedHistoryOnDemandTimeout(HistoryOnDemandRequest state) =>
            _acceptedHistoryTimeout.Schedule(state);

        void IHistorySyncHost.NoteHistorySyncLanded(string syncType, bool hasContent)
        {
            Host.EnableScheduledPersist("history sync received");
            if (!hasContent || _historyIdentityRefreshTriggeredThisSession)
            {
                return;
            }

            if (_syncWork.IsReplayDrainActive)
            {
                Debug.WriteLine(
                    LogPrefix + "Deferring one-shot identity refresh until replay drain completes.");
                return;
            }

            _historyIdentityRefreshTriggeredThisSession = true;
            Debug.WriteLine(
                LogPrefix + "Scheduling post-message enrichment after first non-empty history sync.");
            _heavyHistoryEnrichment.Run(syncType ?? "history-sync");
        }

        // --- IHistoryCatchUpHost ---------------------------------------------

        DateTime IHistoryCatchUpHost.GetNewestStoredMessageUtc() => GetNewestStoredMessageUtc();

        bool IHistoryCatchUpHost.TryGetHistoryFreshnessStaleReason(DateTime nowUtc, out string reason) =>
            TryGetHistoryFreshnessStaleReason(nowUtc, out reason);

        bool IHistoryCatchUpHost.IsCatchUpBannerActive => IsHistoryCatchUpBannerActive;

        bool IHistoryCatchUpHost.CanSolicitAdjacent =>
            !_sessionFlags.SuppressReconnect && _authSlot.Current != null && _authSlot.Current.Registered;

        void IHistoryCatchUpHost.MarkEnrichmentAwaitingHeavyHistory() =>
            _heavyHistoryEnrichment.SetAwaitingHeavyHistory(true);

        void IHistoryCatchUpHost.FinishContinue(string reason, bool releaseEnrichment) =>
            _catchUpFinish.Run(reason, releaseEnrichment);

        void IHistoryCatchUpHost.ClearFullHistoryOnDemandRequest(string reason) =>
            _fullHistoryClear.Run(reason);

        void IHistoryCatchUpHost.NotifySilentFullLotQuiet(string triggerReason, int appliedCount) =>
            _silentChain.OnLotQuiet(triggerReason, appliedCount);

        /// <summary>
        /// Leaves a latch so the next session / offline-complete path can resume FULL_HISTORY
        /// after Mobile hits its adjacent-page cap while tips are still owed.
        /// </summary>
        void IHistoryCatchUpHost.MarkForceHistoryRepairPending(string reason) =>
            _forceHistoryRepair.MarkPending(reason);

        void IHistoryCatchUpHost.StartHydrationFallback(string reason) =>
            _ = _hydrationFallback.RunAsync(reason);

        async Task<bool> IHistoryCatchUpHost.TryRequestAdjacentLiveFullHistoryAsync(string reason)
        {
            if (_socketSlot.Current == null || !_socketSlot.Current.IsHandshakeComplete || !IsConnected)
            {
                return false;
            }

            Debug.WriteLine(LogPrefix + "Adjacent FULL_HISTORY on live socket: " + reason);
            _diagnostics.Write(
                "history",
                "catch-up-adjacent-live",
                "reason=" + (reason ?? string.Empty) +
                "; round=" + _catchUp.Snapshot().Round);

            return await _requestFullTracked.RunAsync(
                reason,
                isFreshnessRepair: true).ConfigureAwait(false);
        }

        void IHistoryCatchUpHost.WriteHistoryJournal(string eventName, string detail) =>
            _diagnostics.Write("history", eventName, detail);

        void IHistoryCatchUpHost.RecordHistoryException(string eventName, Exception error, string detail) =>
            _diagnostics.RecordException("history", eventName, error, detail);

        // --- IHistoryHydrationBackfillHost -----------------------------------

        bool IHistoryHydrationBackfillHost.IsConnected => IsConnected;

        int IHistoryHydrationBackfillHost.HydrationBatchSize => HistoryOnDemandPacing.HydrationBatchSize;

        int IHistoryHydrationBackfillHost.HydrationPageSize => HistoryOnDemandPacing.HydrationPageSize;

        int IHistoryHydrationBackfillHost.HydrationMaxPagesPerChat =>
            HistoryOnDemandPacing.HydrationMaxPagesPerChat;

        IReadOnlyList<HistoryBackfillCandidate> IHistoryHydrationBackfillHost.BuildHydrationCandidates(
            DateTime nowUtc) =>
            _hydrationCandidates.Build(nowUtc);

        int IHistoryHydrationBackfillHost.GetHydrationMessageCount(string jid) =>
            GetHistoryHydrationMessageCount(jid);

        string IHistoryHydrationBackfillHost.NormalizeHydrationJid(string jid) =>
            JidHelper.Normalize(jid);

        Task<bool> IHistoryHydrationBackfillHost.RequestHydrationOnDemandAsync(
            string normJid,
            int pageSize,
            string triggerReason) =>
            RequestHistoryOnDemandIfNeededAsync(normJid, pageSize, triggerReason);

        Task IHistoryHydrationBackfillHost.WaitForHydrationSettleAsync() =>
            _hydrationSettle.WaitAsync();

        void IHistoryHydrationBackfillHost.LogHydrationSkippedNotConnected(string reason) =>
            Debug.WriteLine(
                LogPrefix + "History hydration skipped: not connected (" + reason + ")");

        void IHistoryHydrationBackfillHost.LogHydrationNoCandidates(string reason) =>
            Debug.WriteLine(
                LogPrefix + "History hydration: no stale chats with cursor (" + reason + ")");

        void IHistoryHydrationBackfillHost.WriteHydrationStart(
            string reason,
            int chatCount,
            int batchSize,
            int maxPages)
        {
            Debug.WriteLine(
                LogPrefix + "History hydration starting: chats=" + chatCount +
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
                LogPrefix + "History hydration done: requested=" + requested +
                " synced=" + synced +
                " hasMoreLeft=" + hasMoreLeft +
                " failed=" + failed);

        // --- ISqliteHistoryQuietFinalizeHost ---------------------------------

        int ISqliteHistoryQuietFinalizeHost.SqliteHistoryConversationsAccumulated
        {
            get => _historyChunks.ConversationsAccumulated;
            set => _historyChunks.ConversationsAccumulated = value;
        }

        bool ISqliteHistoryQuietFinalizeHost.FirstSyncListReleaseDeferred
        {
            get => _firstSyncHold.ReleaseDeferred;
            set => _firstSyncHold.ReleaseDeferred = value;
        }

        bool ISqliteHistoryQuietFinalizeHost.IsHistoryCatchUpActive => IsHistoryCatchUpActive;

        bool ISqliteHistoryQuietFinalizeHost.IsHistoryCatchUpBannerActive =>
            IsHistoryCatchUpBannerActive;

        bool ISqliteHistoryQuietFinalizeHost.EnrichmentAwaitingHeavyHistory =>
            _heavyHistoryEnrichment.IsAwaitingHeavyHistory;

        bool ISqliteHistoryQuietFinalizeHost.IsFirstSyncListHoldInProgress =>
            IsFirstSyncListHoldInProgress;

        bool ISqliteHistoryQuietFinalizeHost.IsListPaintPending => !_listPaintGate.IsReleased;

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
            _initialSyncProgress.Run(isActive, isCompleted, processed, total, reason);

        void ISqliteHistoryQuietFinalizeHost.RequestFirstSyncListReleaseAfterNames(
            string reason,
            int processed,
            int total) =>
            _firstSyncListRelease.Schedule(reason, processed, total);

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
            _catchUpBanner.End(reason);

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
            IChatService chats = _resolveChats();
            if (chats != null)
            {
                await chats.ReconcileChatPreviewsFromSqliteAsync(null, "history-quiet")
                    .ConfigureAwait(false);
            }
        }

        void ISqliteHistoryQuietFinalizeHost.LogPreviewReconcileFailed(Exception ex) =>
            Debug.WriteLine(
                LogPrefix + "Post-sync preview reconcile failed: " +
                (ex != null ? ex.Message : string.Empty));

        void ISqliteHistoryQuietFinalizeHost.LogFinalizeFailed(Exception ex) =>
            Debug.WriteLine(
                LogPrefix + "FinalizeSqliteHistoryProgress failed: " +
                (ex != null ? ex.Message : string.Empty));

        void ISqliteHistoryQuietFinalizeHost.LogQuietLotHandOff(
            string lotRequestId,
            int lotProgressSignals,
            int processed,
            bool bannerActive,
            bool deferListRelease) =>
            Debug.WriteLine(
                LogPrefix + "SQLite quiet lot handed to catch-up continue: requestId=" +
                (lotRequestId ?? "<none>") +
                ", signals=" + lotProgressSignals +
                ", processed=" + processed +
                ", banner=" + bannerActive +
                ", deferListRelease=" + deferListRelease);

        // --- IHydrationDayRetryIoHost ----------------------------------------

        bool IHydrationDayRetryIoHost.TryGetFreshnessStaleReason(
            DateTime nowUtc,
            out string staleReason) =>
            TryGetHistoryFreshnessStaleReason(nowUtc, out staleReason);

        /// <summary>
        /// After FULL_HISTORY quiet fails while tips stay stale: per-JID ON_DEMAND hydration
        /// (Pending → syncing → HasMore/Synced), then natural FULL_HISTORY again. Day retries
        /// keep going until tips look fresh (max 5 / day, every 3h).
        /// </summary>
        Task IHydrationDayRetryIoHost.RunHydrationFallbackPipelineAsync(string reason) =>
            _hydrationFallback.RunAsync(reason);

        void IHydrationDayRetryIoHost.StampLastHydrationFallbackUtc(DateTime nowUtc) =>
            _lastHydrationFallbackUtc = nowUtc;

        void IHydrationDayRetryIoHost.LogHydrationDayRetry(string message) =>
            Debug.WriteLine(LogPrefix + message);
    }
}
