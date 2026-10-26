// =============================================================================
// IFullHistoryCatchUpWatchdogHost
//
// Side effects for FullHistoryCatchUpWatchdogDispatch.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.State;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IFullHistoryCatchUpWatchdogHost
    {
        TimeSpan FullHistoryCatchUpIdleAbort { get; }

        TimeSpan FullHistoryCatchUpProgressPoll { get; }

        TimeSpan FullHistoryOnDemandResponseTimeout { get; }

        int GetStoredMessageCount();

        bool IsHistoryCatchUpBannerActive { get; }

        void WriteCatchUpWatchdogJournal(string eventName, string detail);

        Task<HistoryCatchUpWatchdogExit> WatchCatchUpAsync(
            HistoryOnDemandRequest state,
            Action<HistoryCatchUpWatchdogTick> onTick);

        void NotifyCatchUpIdleBatch(
            string requestId,
            bool ackAccepted,
            int progressSignals,
            string triggerReason,
            int idleMs);

        void LogCatchUpWatchdog(string message);

        void RecordCatchUpWatchdogException(Exception error, string requestId);

        void FireAndForgetCatchUpWatchdog(Func<Task> work);
    }
}
