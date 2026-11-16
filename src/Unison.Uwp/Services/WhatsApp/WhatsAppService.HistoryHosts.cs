using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;
using Proto;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Unison.WhatsAppClient.History;

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
            get => _firstSyncHold.ReleaseDeferred;
            set => _firstSyncHold.ReleaseDeferred = value;
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

        /// <summary>
        /// Stamps progress on the in-flight FULL_HISTORY catch-up so the idle watchdog does not
        /// abort while tips/chunks are still arriving on Mobile. ACK/banner only refresh the idle
        /// clock; they must not bump <see cref="HistoryOnDemandRequest.ProgressSignalCount"/>.
        /// </summary>
        private void NoteFullHistoryCatchUpProgress(string reason)
        {
            _catchUpProgress.Note(reason);
            MaybeBeginSilentCatchUpBannerFromProgress(reason);
        }

        private void RememberLastHistorySyncType(string type)
        {
            Proto.HistorySync.Types.HistorySyncType parsed;
            if (Enum.TryParse(type, ignoreCase: true, out parsed))
            {
                _lastHistorySyncTypeReceived = parsed;
            }
        }

        /// <summary>
        /// After a FULL_HISTORY request goes quiet: soft-reconnect for the next offline lot while
        /// the lot still made progress (chunk signals or tip watermark). Tip freshness alone must
        /// not stop catch-up ? older conversations often arrive without moving the newest tip.
        /// Keep insisting until several idle cycles show no progress, or max rounds is hit.
        /// </summary>
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

                Debug.WriteLine(
                        "[WhatsAppService] Adjacent FULL_HISTORY on live socket: " + reason);
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

        private bool IsHistoryCatchUpActive()
        {
            return _catchUp.IsActive(IsHistoryCatchUpBannerActive);
        }

        void IConnectionSoftReconnectHost.ScheduleSoftReconnect(string reason) =>
            _softReconnect.Schedule(reason);

        /// <summary>
        /// Soft-reconnect to pull another offline catch-up lot while the history banner stays up.
        /// Distinct from the long-cooldown freshness failure fallback.
        /// </summary>
        void IConnectionSoftReconnectHost.ScheduleCatchUpContinueReconnect(string reason) =>
            _historyReconnectFallback.ScheduleCatchUpContinue(reason);

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
            EnableScheduledPersist("history sync received");
            if (!hasContent || _historyIdentityRefreshTriggeredThisSession)
            {
                return;
            }

            if (_syncWork.IsReplayDrainActive)
            {
                Debug.WriteLine(
                    "[WhatsAppService] Deferring one-shot identity refresh until replay drain completes.");
                return;
            }

            _historyIdentityRefreshTriggeredThisSession = true;
            Debug.WriteLine(
                "[WhatsAppService] Scheduling post-message enrichment after first non-empty history sync.");
            _heavyHistoryEnrichment.Run(syncType ?? "history-sync");
        }

        private HistoryOnDemandDispatch HistoryDispatch =>
            _historyDispatch ??= new HistoryOnDemandDispatch(
                _historyRequests,
                _historyOnDemandSocket,
                line => Debug.WriteLine("[WhatsAppService] " + line));

        private Task<bool> RequestHistoryOnDemandIfNeededAsync(string normJid, int count, string triggerReason = null)
        {
            if (string.IsNullOrWhiteSpace(normJid))
            {
                return Task.FromResult(false);
            }

            // Same seam as HistoryFacade: store snapshot, not the live cache list.
            return HistoryDispatch.RequestChatPageAsync(
                normJid,
                _chatState.GetMessages(normJid),
                count,
                triggerReason,
                DateTime.UtcNow);
        }

        private void MaybeBeginSilentCatchUpBannerFromProgress(string reason)
        {
            // Do not gate on the silent chain being armed: after empty-cap it disarms, but
            // residual silent applies (or late chunks) should still raise the banner.
            if (string.IsNullOrEmpty(reason))
            {
                return;
            }

            if (reason.IndexOf("sqlite-applied:", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return;
            }

            int conversations = 0;
            int idx = reason.IndexOf("conversations=", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                string tail = reason.Substring(idx + "conversations=".Length);
                int end = 0;
                while (end < tail.Length && char.IsDigit(tail[end]))
                {
                    end++;
                }

                if (end > 0)
                {
                    int.TryParse(tail.Substring(0, end), out conversations);
                }
            }

            if (conversations <= 0)
                {
                    return;
                }

            _historyRequests.ReadFullDiagnostics(out _, out _, out HistoryOnDemandRequest tracked);
            if (tracked == null ||
                !SilentFullHistoryPolicy.IsSilentReason(tracked.TriggerReason))
            {
                return;
            }

            _catchUpBanner.Begin(tracked.TriggerReason);
        }

        // --- IHydrationDayRetryIoHost ----------------------------------------

        bool IHydrationDayRetryIoHost.TryGetFreshnessStaleReason(
            DateTime nowUtc,
            out string staleReason) =>
            TryGetHistoryFreshnessStaleReason(nowUtc, out staleReason);

        /// <summary>
        /// After FULL_HISTORY quiet fails while tips stay stale: per-JID ON_DEMAND hydration
        /// (Pending ? syncing ? HasMore/Synced), then natural FULL_HISTORY again. Day retries
        /// keep going until tips look fresh (max 5 / day, every 3h).
        /// </summary>
        Task IHydrationDayRetryIoHost.RunHydrationFallbackPipelineAsync(string reason) =>
            _hydrationFallback.RunAsync(reason);

        void IHydrationDayRetryIoHost.StampLastHydrationFallbackUtc(DateTime nowUtc) =>
            _lastHydrationFallbackUtc = nowUtc;

        void IHydrationDayRetryIoHost.LogHydrationDayRetry(string message) =>
            Debug.WriteLine("[WhatsAppService] " + message);

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

    }
}
