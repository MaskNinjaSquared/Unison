// =============================================================================
// HistoryPostOfflineFreshnessOwner
//
// Owns IHistoryPostOfflineFreshnessHost: after the offline drain, checks tip
// freshness and either continues catch-up, finishes it, or asks FULL_HISTORY.
// Stored-message probes and the last-sync stamps stay ports to the client.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class HistoryPostOfflineFreshnessOwner : IHistoryPostOfflineFreshnessHost
    {
        private readonly HistoryCatchUpContinuation _catchUp;
        private readonly ReleaseEnrichmentAfterHeavyHistoryCoordinator _heavyHistoryEnrichment;
        private readonly Func<IHistoryService> _history;
        private readonly Func<DateTime> _lastHistorySyncReceivedUtc;
        private readonly Func<string> _lastHistorySyncTypeOrUnknown;
        private readonly Func<bool> _isHistoryCatchUpActive;
        private readonly Func<int> _catchUpStagnantCyclesBeforeStop;
        private readonly Action<string> _noteCatchUpProgress;
        private readonly TryGetHistoryStaleReason _tryGetStaleReason;
        private readonly Func<DateTime> _newestStoredMessageUtc;
        private readonly Func<DateTime> _newestNonSelfStoredMessageUtc;
        private readonly Func<DateTime> _newestGroupStoredMessageUtc;
        private readonly Func<string, Task> _requestReconnectHistoryCatchUpAsync;
        private readonly Action<string, bool> _finishHistoryCatchUpContinue;
        private readonly Action<string> _log;

        public HistoryPostOfflineFreshnessOwner(
            HistoryCatchUpContinuation catchUp,
            ReleaseEnrichmentAfterHeavyHistoryCoordinator heavyHistoryEnrichment,
            Func<IHistoryService> history,
            Func<DateTime> lastHistorySyncReceivedUtc,
            Func<string> lastHistorySyncTypeOrUnknown,
            Func<bool> isHistoryCatchUpActive,
            Func<int> catchUpStagnantCyclesBeforeStop,
            Action<string> noteCatchUpProgress,
            TryGetHistoryStaleReason tryGetStaleReason,
            Func<DateTime> newestStoredMessageUtc,
            Func<DateTime> newestNonSelfStoredMessageUtc,
            Func<DateTime> newestGroupStoredMessageUtc,
            Func<string, Task> requestReconnectHistoryCatchUpAsync,
            Action<string, bool> finishHistoryCatchUpContinue,
            Action<string> log)
        {
            _catchUp = catchUp ?? throw new ArgumentNullException(nameof(catchUp));
            _heavyHistoryEnrichment = heavyHistoryEnrichment
                ?? throw new ArgumentNullException(nameof(heavyHistoryEnrichment));
            _history = history ?? throw new ArgumentNullException(nameof(history));
            _lastHistorySyncReceivedUtc = lastHistorySyncReceivedUtc
                ?? throw new ArgumentNullException(nameof(lastHistorySyncReceivedUtc));
            _lastHistorySyncTypeOrUnknown = lastHistorySyncTypeOrUnknown
                ?? throw new ArgumentNullException(nameof(lastHistorySyncTypeOrUnknown));
            _isHistoryCatchUpActive = isHistoryCatchUpActive
                ?? throw new ArgumentNullException(nameof(isHistoryCatchUpActive));
            _catchUpStagnantCyclesBeforeStop = catchUpStagnantCyclesBeforeStop
                ?? throw new ArgumentNullException(nameof(catchUpStagnantCyclesBeforeStop));
            _noteCatchUpProgress = noteCatchUpProgress
                ?? throw new ArgumentNullException(nameof(noteCatchUpProgress));
            _tryGetStaleReason = tryGetStaleReason
                ?? throw new ArgumentNullException(nameof(tryGetStaleReason));
            _newestStoredMessageUtc = newestStoredMessageUtc
                ?? throw new ArgumentNullException(nameof(newestStoredMessageUtc));
            _newestNonSelfStoredMessageUtc = newestNonSelfStoredMessageUtc
                ?? throw new ArgumentNullException(nameof(newestNonSelfStoredMessageUtc));
            _newestGroupStoredMessageUtc = newestGroupStoredMessageUtc
                ?? throw new ArgumentNullException(nameof(newestGroupStoredMessageUtc));
            _requestReconnectHistoryCatchUpAsync = requestReconnectHistoryCatchUpAsync
                ?? throw new ArgumentNullException(nameof(requestReconnectHistoryCatchUpAsync));
            _finishHistoryCatchUpContinue = finishHistoryCatchUpContinue
                ?? throw new ArgumentNullException(nameof(finishHistoryCatchUpContinue));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public Task RunAsync(int offlineCount) => HistoryPostOfflineFreshnessDispatch.RunAsync(this, offlineCount);

        DateTime IHistoryPostOfflineFreshnessHost.LastHistorySyncReceivedUtc => _lastHistorySyncReceivedUtc();

        string IHistoryPostOfflineFreshnessHost.LastHistorySyncTypeOrUnknown => _lastHistorySyncTypeOrUnknown();

        bool IHistoryPostOfflineFreshnessHost.IsHistoryCatchUpActive => _isHistoryCatchUpActive();

        bool IHistoryPostOfflineFreshnessHost.IsPairingBootstrapActive =>
            _history()?.IsPairingBootstrapActive ?? false;

        int IHistoryPostOfflineFreshnessHost.CatchUpStagnantCyclesBeforeStop =>
            _catchUpStagnantCyclesBeforeStop();

        void IHistoryPostOfflineFreshnessHost.NoteCatchUpProgress(string reason) => _noteCatchUpProgress(reason);

        bool IHistoryPostOfflineFreshnessHost.TryGetFreshnessStaleReason(
            DateTime nowUtc,
            out string staleReason) =>
            _tryGetStaleReason(nowUtc, out staleReason);

        DateTime IHistoryPostOfflineFreshnessHost.GetNewestStoredMessageUtc() => _newestStoredMessageUtc();

        DateTime IHistoryPostOfflineFreshnessHost.GetNewestNonSelfStoredMessageUtc() =>
            _newestNonSelfStoredMessageUtc();

        DateTime IHistoryPostOfflineFreshnessHost.GetNewestGroupStoredMessageUtc() =>
            _newestGroupStoredMessageUtc();

        void IHistoryPostOfflineFreshnessHost.MarkEnrichmentAwaitingHeavyHistory() =>
            _heavyHistoryEnrichment.SetAwaitingHeavyHistory(true);

        HistoryCatchUpDrainAction IHistoryPostOfflineFreshnessHost.DecideAfterFreshDrain(
            int offlineCount,
            int stagnantStop,
            out int stagnantCycles) =>
            _catchUp.DecideAfterFreshDrain(offlineCount, stagnantStop, out stagnantCycles);

        Task IHistoryPostOfflineFreshnessHost.RequestReconnectHistoryCatchUpAsync(string reason) =>
            _requestReconnectHistoryCatchUpAsync(reason);

        void IHistoryPostOfflineFreshnessHost.FinishHistoryCatchUpContinue(
            string reason,
            bool releaseEnrichment) =>
            _finishHistoryCatchUpContinue(reason, releaseEnrichment);

        void IHistoryPostOfflineFreshnessHost.LogSyncObservedThisSession(
            DateTime syncUtc,
            string syncType) =>
            _log(
                "Post-offline freshness note: history sync observed this session at " +
                syncUtc.ToString("O") +
                " (type=" + syncType +
                "); freshness is evaluated by newest stored message age.");

        void IHistoryPostOfflineFreshnessHost.LogFreshnessPassed(
            string anyText,
            string nonSelfText,
            string groupText,
            int offlineCount,
            bool catchUpActive) =>
            _log(
                "Post-offline freshness check passed: message freshness scopes are fresh (any=" +
                anyText + ", nonSelf=" + nonSelfText + ", group=" + groupText +
                ", offlineCount=" + offlineCount +
                ", catchUpActive=" + catchUpActive + ")");

        void IHistoryPostOfflineFreshnessHost.LogCatchUpEmptyWhileTipFresh(
            int stagnantCycles,
            int stagnantStop) =>
            _log(
                "Catch-up offline drain empty while tip fresh; stagnant=" +
                stagnantCycles + "/" + stagnantStop);

        void IHistoryPostOfflineFreshnessHost.LogFreshnessNotStaleSkip(
            string staleReason,
            string newestText,
            string tipAgeMinutes,
            int offlineCount,
            bool catchUpActive) =>
            _log(
                "Post-offline freshness not stale; skipping FULL_HISTORY " +
                "(staleReason=" + staleReason +
                ", newestStored=" + newestText +
                ", tipAgeMinutes=" + tipAgeMinutes +
                ", offlineCount=" + offlineCount +
                ", catchUpActive=" + catchUpActive + ")");

        void IHistoryPostOfflineFreshnessHost.LogFreshnessStillStaleRequest(
            string staleReason,
            string newestText,
            int offlineCount) =>
            _log(
                "Post-offline freshness check remains stale after normal replay: staleReason=" +
                staleReason +
                ", newestStored=" + newestText +
                ", offlineCount=" + offlineCount +
                ". Requesting FULL_HISTORY_SYNC_ON_DEMAND before name/group enrichment.");
    }
}
