// =============================================================================
// FullHistoryCatchUpWatchdogOwner
//
// Owns IFullHistoryCatchUpWatchdogHost and the lazy HistoryCatchUpWatchdog.
// The idle-abort window is read once, on first watch, like the client did.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class FullHistoryCatchUpWatchdogOwner : IFullHistoryCatchUpWatchdogHost
    {
        private readonly HistoryOnDemandRegistry _historyRequests;
        private readonly SyncStatusChannel _syncStatus;
        private readonly Func<IHistoryService> _history;
        private readonly Func<TimeSpan> _idleAbort;
        private readonly TimeSpan _progressPoll;
        private readonly TimeSpan _responseTimeout;
        private readonly Func<int> _storedMessageCount;
        private readonly IRuntimeDiagnostics _diagnostics;
        private readonly Action<string> _log;
        private HistoryCatchUpWatchdog _watchdog;

        public FullHistoryCatchUpWatchdogOwner(
            HistoryOnDemandRegistry historyRequests,
            SyncStatusChannel syncStatus,
            Func<IHistoryService> history,
            Func<TimeSpan> idleAbort,
            TimeSpan progressPoll,
            TimeSpan responseTimeout,
            Func<int> storedMessageCount,
            IRuntimeDiagnostics diagnostics,
            Action<string> log)
        {
            _historyRequests = historyRequests
                ?? throw new ArgumentNullException(nameof(historyRequests));
            _syncStatus = syncStatus ?? throw new ArgumentNullException(nameof(syncStatus));
            _history = history ?? throw new ArgumentNullException(nameof(history));
            _idleAbort = idleAbort ?? throw new ArgumentNullException(nameof(idleAbort));
            _progressPoll = progressPoll;
            _responseTimeout = responseTimeout;
            _storedMessageCount = storedMessageCount
                ?? throw new ArgumentNullException(nameof(storedMessageCount));
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public void Schedule(HistoryOnDemandRequest state) =>
            FullHistoryCatchUpWatchdogDispatch.Schedule(this, state);

        private HistoryCatchUpWatchdog Watchdog =>
            _watchdog ?? (_watchdog = new HistoryCatchUpWatchdog(_historyRequests, _progressPoll, _idleAbort()));

        TimeSpan IFullHistoryCatchUpWatchdogHost.FullHistoryCatchUpIdleAbort => _idleAbort();

        TimeSpan IFullHistoryCatchUpWatchdogHost.FullHistoryCatchUpProgressPoll => _progressPoll;

        TimeSpan IFullHistoryCatchUpWatchdogHost.FullHistoryOnDemandResponseTimeout => _responseTimeout;

        int IFullHistoryCatchUpWatchdogHost.GetStoredMessageCount() => _storedMessageCount();

        bool IFullHistoryCatchUpWatchdogHost.IsHistoryCatchUpBannerActive =>
            _syncStatus.IsHistoryCatchUpShown;

        void IFullHistoryCatchUpWatchdogHost.WriteCatchUpWatchdogJournal(
            string eventName,
            string detail) =>
            _diagnostics.Write("history", eventName, detail);

        Task<HistoryCatchUpWatchdogExit> IFullHistoryCatchUpWatchdogHost.WatchCatchUpAsync(
            HistoryOnDemandRequest state,
            Action<HistoryCatchUpWatchdogTick> onTick) =>
            Watchdog.WatchAsync(state, onTick);

        void IFullHistoryCatchUpWatchdogHost.NotifyCatchUpIdleBatch(
            string requestId,
            bool ackAccepted,
            int progressSignals,
            string triggerReason,
            int idleMs) =>
            _history()?.NotifyCatchUpIdleBatch(
                requestId,
                ackAccepted,
                progressSignals,
                triggerReason,
                idleMs);

        void IFullHistoryCatchUpWatchdogHost.LogCatchUpWatchdog(string message) => _log(message);

        void IFullHistoryCatchUpWatchdogHost.RecordCatchUpWatchdogException(
            Exception error,
            string requestId) =>
            _diagnostics.RecordException(
                "history",
                "catch-up-watchdog-failed",
                error,
                "requestId=" + requestId);

        void IFullHistoryCatchUpWatchdogHost.FireAndForgetCatchUpWatchdog(Func<Task> work) =>
            _ = Task.Run(work);
    }
}
