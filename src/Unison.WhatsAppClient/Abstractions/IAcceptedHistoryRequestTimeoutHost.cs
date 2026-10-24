// =============================================================================
// IAcceptedHistoryRequestTimeoutHost
//
// Side effects for AcceptedHistoryRequestTimeoutDispatch.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.State;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IAcceptedHistoryRequestTimeoutHost
    {
        bool TryArmHistoryRequestTimeout(
            HistoryOnDemandRequest state,
            out Task<HistoryOnDemandRequest> expiry);

        TimeSpan HistoryRequestTimeoutWindow(HistoryOnDemandRequest state);

        void ScheduleFullHistoryCatchUpWatchdog(HistoryOnDemandRequest state);

        int GetStoredMessageCount();

        int GetChatMessageCount(string chatJid);

        void LogHistoryRequestTimeout(string message);

        void WriteCatchUpHardTimeoutJournal(
            string requestId,
            int progressSignals,
            string lastProgressReason,
            int baselineMessageCount,
            int currentMessageCount,
            string triggerReason);

        void NotifyCatchUpIdleBatch(
            string requestId,
            bool ackAccepted,
            int progressSignals,
            string triggerReason,
            int idleMs);

        void FireAndForgetHistoryRequestTimeout(Func<Task> work);
    }
}
