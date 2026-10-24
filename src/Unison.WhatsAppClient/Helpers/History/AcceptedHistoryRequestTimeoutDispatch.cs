// =============================================================================
// AcceptedHistoryRequestTimeoutDispatch
//
// Body of ScheduleAcceptedHistoryRequestTimeout: arm timer, optional FULL
// watchdog, then on expiry log / hard-timeout catch-up. Task.Run on the host.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class AcceptedHistoryRequestTimeoutDispatch
    {
        public static void Schedule(
            IAcceptedHistoryRequestTimeoutHost host,
            HistoryOnDemandRequest state)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (state == null)
            {
                return;
            }

            Task<HistoryOnDemandRequest> expiry;
            if (!host.TryArmHistoryRequestTimeout(state, out expiry))
            {
                return;
            }

            bool isFullHistory = state.Kind == HistoryOnDemandKind.FullHistory;
            TimeSpan timeout = host.HistoryRequestTimeoutWindow(state);

            if (isFullHistory)
            {
                host.ScheduleFullHistoryCatchUpWatchdog(state);
            }

            HistoryOnDemandRequest stateCapture = state;
            host.FireAndForgetHistoryRequestTimeout(
                () => RunOnExpiryAsync(host, stateCapture, expiry, isFullHistory, timeout));
        }

        public static async Task RunOnExpiryAsync(
            IAcceptedHistoryRequestTimeoutHost host,
            HistoryOnDemandRequest state,
            Task<HistoryOnDemandRequest> expiry,
            bool isFullHistory,
            TimeSpan timeout)
        {
            if (host == null || state == null || expiry == null)
            {
                return;
            }

            HistoryOnDemandRequest expired = await expiry.ConfigureAwait(false);
            if (expired == null)
            {
                return;
            }

            int progressSignals = expired.ProgressSignalCount;
            string lastProgressReason = expired.LastProgressReason;
            DateTime lastProgressUtc = expired.LastProgressUtc;
            int currentMessageCount = isFullHistory
                ? host.GetStoredMessageCount()
                : host.GetChatMessageCount(state.ChatJid);

            host.LogHistoryRequestTimeout(
                state.TypeName + " timed out: requestId=" + state.RequestId +
                ", chat=" + (state.ChatJid ?? "<full-history>") +
                ", baseline=" + state.BaselineMessageCount +
                ", current=" + currentMessageCount +
                ", timeoutMs=" + (int)timeout.TotalMilliseconds +
                ", signals=" + progressSignals +
                ", lastProgress=" + (lastProgressReason ?? "<none>") +
                ", lastProgressUtc=" + (lastProgressUtc == DateTime.MinValue
                    ? "<none>"
                    : lastProgressUtc.ToString("O")) +
                ", trigger=" + (state.TriggerReason ?? "unspecified"));

            if (!isFullHistory)
            {
                return;
            }

            host.LogHistoryRequestTimeout(
                "FullHistorySyncOnDemand hard timeout: evaluating catch-up continue.");
            host.WriteCatchUpHardTimeoutJournal(
                state.RequestId,
                progressSignals,
                lastProgressReason,
                state.BaselineMessageCount,
                currentMessageCount,
                state.TriggerReason);
            host.NotifyCatchUpIdleBatch(
                state.RequestId,
                expired.AckAccepted,
                progressSignals,
                state.TriggerReason ?? "hard-timeout",
                (int)timeout.TotalMilliseconds);
        }
    }
}
