// =============================================================================
// HistoryCatchUpFinishOwner
//
// Owns IHistoryCatchUpFinishHost. Reads catch-up / initial-sync / chunk state
// directly; first-sync release, deferred avatars and the silent chain stay ports.
// =============================================================================
using System;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class HistoryCatchUpFinishOwner : IHistoryCatchUpFinishHost
    {
        private readonly HistoryCatchUpContinuation _catchUp;
        private readonly InitialSyncProgressState _initialSync;
        private readonly HistorySqliteChunkOwner _historyChunks;
        private readonly ReleaseEnrichmentAfterHeavyHistoryCoordinator _heavyHistoryEnrichment;
        private readonly Func<IHistoryService> _history;
        private readonly Func<bool> _isFirstSyncListHoldInProgress;
        private readonly Func<bool> _firstSyncListReleaseDeferred;
        private readonly Action<string> _endCatchUpBanner;
        private readonly Action<string, int, int> _requestFirstSyncListRelease;
        private readonly Action<string, TimeSpan> _scheduleDeferredAvatars;
        private readonly Action<string> _trySchedulePostMessageEnrichment;
        private readonly Action<string> _notifySilentChainAfterLoudFinish;
        private readonly IRuntimeDiagnostics _diagnostics;

        public HistoryCatchUpFinishOwner(
            HistoryCatchUpContinuation catchUp,
            InitialSyncProgressState initialSync,
            HistorySqliteChunkOwner historyChunks,
            ReleaseEnrichmentAfterHeavyHistoryCoordinator heavyHistoryEnrichment,
            Func<IHistoryService> history,
            Func<bool> isFirstSyncListHoldInProgress,
            Func<bool> firstSyncListReleaseDeferred,
            Action<string> endCatchUpBanner,
            Action<string, int, int> requestFirstSyncListRelease,
            Action<string, TimeSpan> scheduleDeferredAvatars,
            Action<string> trySchedulePostMessageEnrichment,
            Action<string> notifySilentChainAfterLoudFinish,
            IRuntimeDiagnostics diagnostics)
        {
            _catchUp = catchUp ?? throw new ArgumentNullException(nameof(catchUp));
            _initialSync = initialSync ?? throw new ArgumentNullException(nameof(initialSync));
            _historyChunks = historyChunks ?? throw new ArgumentNullException(nameof(historyChunks));
            _heavyHistoryEnrichment = heavyHistoryEnrichment
                ?? throw new ArgumentNullException(nameof(heavyHistoryEnrichment));
            _history = history ?? throw new ArgumentNullException(nameof(history));
            _isFirstSyncListHoldInProgress = isFirstSyncListHoldInProgress
                ?? throw new ArgumentNullException(nameof(isFirstSyncListHoldInProgress));
            _firstSyncListReleaseDeferred = firstSyncListReleaseDeferred
                ?? throw new ArgumentNullException(nameof(firstSyncListReleaseDeferred));
            _endCatchUpBanner = endCatchUpBanner ?? throw new ArgumentNullException(nameof(endCatchUpBanner));
            _requestFirstSyncListRelease = requestFirstSyncListRelease
                ?? throw new ArgumentNullException(nameof(requestFirstSyncListRelease));
            _scheduleDeferredAvatars = scheduleDeferredAvatars
                ?? throw new ArgumentNullException(nameof(scheduleDeferredAvatars));
            _trySchedulePostMessageEnrichment = trySchedulePostMessageEnrichment
                ?? throw new ArgumentNullException(nameof(trySchedulePostMessageEnrichment));
            _notifySilentChainAfterLoudFinish = notifySilentChainAfterLoudFinish
                ?? throw new ArgumentNullException(nameof(notifySilentChainAfterLoudFinish));
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        }

        public void Run(string reason, bool releaseEnrichment) =>
            HistoryCatchUpFinishDispatch.Run(this, reason, releaseEnrichment);

        HistoryCatchUpProgress IHistoryCatchUpFinishHost.FinishCatchUpProgress() => _catchUp.Finish();

        void IHistoryCatchUpFinishHost.WriteCatchUpFinishJournal(
            string reason,
            HistoryCatchUpProgress last) =>
            _diagnostics.Write(
                "history",
                "catch-up-finish",
                "reason=" + (reason ?? string.Empty) +
                "; rounds=" + last.Round +
                "; stagnant=" + last.StagnantCycles +
                "; watermark=" + FreshnessTimestampFormat.ForLog(last.WatermarkUtc));

        void IHistoryCatchUpFinishHost.EndHistoryCatchUpBanner(string reason) => _endCatchUpBanner(reason);

        bool IHistoryCatchUpFinishHost.IsFirstSyncListHoldInProgress => _isFirstSyncListHoldInProgress();

        bool IHistoryCatchUpFinishHost.IsListAwaitingFirstHistorySync =>
            _history()?.IsAwaitingFirstHistorySync ?? false;

        bool IHistoryCatchUpFinishHost.IsPairingBootstrapChunkSeenOrApplying =>
            _history()?.IsPairingBootstrapChunkSeenOrApplying ?? false;

        bool IHistoryCatchUpFinishHost.FirstSyncListReleaseDeferred => _firstSyncListReleaseDeferred();

        bool IHistoryCatchUpFinishHost.IsHistoryApplyBusy => HistorySyncWireApply.IsApplyBusy;

        int IHistoryCatchUpFinishHost.SqliteHistoryConversationsAccumulated =>
            _historyChunks.ConversationsAccumulated;

        int IHistoryCatchUpFinishHost.InitialSyncProcessedConversations =>
            _initialSync.ProcessedConversations;

        void IHistoryCatchUpFinishHost.RequestFirstSyncListReleaseAfterNames(
            string reason,
            int processed,
            int total) =>
            _requestFirstSyncListRelease(reason, processed, total);

        void IHistoryCatchUpFinishHost.ScheduleDeferredAvatars(string reason, TimeSpan delay) =>
            _scheduleDeferredAvatars(reason, delay);

        void IHistoryCatchUpFinishHost.ClearEnrichmentAwaitingHeavyHistory() =>
            _heavyHistoryEnrichment.SetAwaitingHeavyHistory(false);

        void IHistoryCatchUpFinishHost.TrySchedulePostMessageEnrichment(string reason) =>
            _trySchedulePostMessageEnrichment(reason);

        void IHistoryCatchUpFinishHost.NotifySilentChainAfterLoudCatchUpFinish(string finishReason) =>
            _notifySilentChainAfterLoudFinish(finishReason);
    }
}
