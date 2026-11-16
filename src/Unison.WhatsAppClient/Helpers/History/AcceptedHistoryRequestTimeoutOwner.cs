// =============================================================================
// AcceptedHistoryRequestTimeoutOwner
//
// Owns IAcceptedHistoryRequestTimeoutHost and the lazy HistoryOnDemandTimeout.
// Accepted FULL_HISTORY requests hand off to the catch-up watchdog.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class AcceptedHistoryRequestTimeoutOwner : IAcceptedHistoryRequestTimeoutHost
    {
        private readonly HistoryOnDemandRegistry _historyRequests;
        private readonly TimeSpan _chatResponseTimeout;
        private readonly TimeSpan _fullResponseTimeout;
        private readonly FullHistoryCatchUpWatchdogOwner _watchdog;
        private readonly Func<IHistoryService> _history;
        private readonly Func<int> _storedMessageCount;
        private readonly Func<string, int> _chatMessageCount;
        private readonly IRuntimeDiagnostics _diagnostics;
        private readonly Action<string> _log;
        private HistoryOnDemandTimeout _timeout;

        public AcceptedHistoryRequestTimeoutOwner(
            HistoryOnDemandRegistry historyRequests,
            TimeSpan chatResponseTimeout,
            TimeSpan fullResponseTimeout,
            FullHistoryCatchUpWatchdogOwner watchdog,
            Func<IHistoryService> history,
            Func<int> storedMessageCount,
            Func<string, int> chatMessageCount,
            IRuntimeDiagnostics diagnostics,
            Action<string> log)
        {
            _historyRequests = historyRequests
                ?? throw new ArgumentNullException(nameof(historyRequests));
            _chatResponseTimeout = chatResponseTimeout;
            _fullResponseTimeout = fullResponseTimeout;
            _watchdog = watchdog ?? throw new ArgumentNullException(nameof(watchdog));
            _history = history ?? throw new ArgumentNullException(nameof(history));
            _storedMessageCount = storedMessageCount
                ?? throw new ArgumentNullException(nameof(storedMessageCount));
            _chatMessageCount = chatMessageCount
                ?? throw new ArgumentNullException(nameof(chatMessageCount));
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public void Schedule(HistoryOnDemandRequest state) =>
            AcceptedHistoryRequestTimeoutDispatch.Schedule(this, state);

        private HistoryOnDemandTimeout Timeout =>
            _timeout ?? (_timeout = new HistoryOnDemandTimeout(
                _historyRequests,
                _chatResponseTimeout,
                _fullResponseTimeout));

        bool IAcceptedHistoryRequestTimeoutHost.TryArmHistoryRequestTimeout(
            HistoryOnDemandRequest state,
            out Task<HistoryOnDemandRequest> expiry) =>
            Timeout.TryArm(state, out expiry);

        TimeSpan IAcceptedHistoryRequestTimeoutHost.HistoryRequestTimeoutWindow(
            HistoryOnDemandRequest state) =>
            Timeout.WindowFor(state);

        void IAcceptedHistoryRequestTimeoutHost.ScheduleFullHistoryCatchUpWatchdog(
            HistoryOnDemandRequest state) =>
            _watchdog.Schedule(state);

        int IAcceptedHistoryRequestTimeoutHost.GetStoredMessageCount() => _storedMessageCount();

        int IAcceptedHistoryRequestTimeoutHost.GetChatMessageCount(string chatJid) =>
            _chatMessageCount(chatJid);

        void IAcceptedHistoryRequestTimeoutHost.LogHistoryRequestTimeout(string message) =>
            _log(message);

        void IAcceptedHistoryRequestTimeoutHost.WriteCatchUpHardTimeoutJournal(
            string requestId,
            int progressSignals,
            string lastProgressReason,
            int baselineMessageCount,
            int currentMessageCount,
            string triggerReason) =>
            _diagnostics.Write(
                "history",
                "catch-up-hard-timeout",
                "requestId=" + requestId +
                "; signals=" + progressSignals +
                "; lastProgress=" + (lastProgressReason ?? "<none>") +
                "; baselineMsgs=" + baselineMessageCount +
                "; ramMsgs=" + currentMessageCount +
                "; trigger=" + (triggerReason ?? string.Empty));

        void IAcceptedHistoryRequestTimeoutHost.NotifyCatchUpIdleBatch(
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

        void IAcceptedHistoryRequestTimeoutHost.FireAndForgetHistoryRequestTimeout(Func<Task> work) =>
            _ = Task.Run(work);
    }
}
