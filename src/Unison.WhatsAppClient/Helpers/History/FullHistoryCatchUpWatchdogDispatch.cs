// =============================================================================
// FullHistoryCatchUpWatchdogDispatch
//
// Body of ScheduleFullHistoryCatchUpWatchdog. Watch / Task.Run stay on the host.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class FullHistoryCatchUpWatchdogDispatch
    {
        public static void Schedule(
            IFullHistoryCatchUpWatchdogHost host,
            HistoryOnDemandRequest state)
        {
            if (host == null ||
                state == null ||
                string.IsNullOrWhiteSpace(state.RequestId))
            {
                return;
            }

            HistoryOnDemandRequest capture = state;
            host.FireAndForgetCatchUpWatchdog(() => RunAsync(host, capture));
        }

        public static async Task RunAsync(
            IFullHistoryCatchUpWatchdogHost host,
            HistoryOnDemandRequest state)
        {
            if (host == null || state == null)
            {
                return;
            }

            try
            {
                host.WriteCatchUpWatchdogJournal(
                    "catch-up-watchdog-start",
                    "requestId=" + state.RequestId +
                    "; idleAbortMs=" + (int)host.FullHistoryCatchUpIdleAbort.TotalMilliseconds +
                    "; pollMs=" + (int)host.FullHistoryCatchUpProgressPoll.TotalMilliseconds +
                    "; hardTimeoutMs=" + (int)host.FullHistoryOnDemandResponseTimeout.TotalMilliseconds +
                    "; trigger=" + (state.TriggerReason ?? string.Empty));

                HistoryCatchUpWatchdogExit exit = await host.WatchCatchUpAsync(
                        state,
                        tick => host.WriteCatchUpWatchdogJournal(
                            "catch-up-watchdog-tick",
                            "requestId=" + state.RequestId +
                            "; idleMs=" + (int)tick.Idle.TotalMilliseconds +
                            "; ageMs=" + (int)tick.Age.TotalMilliseconds +
                            "; signals=" + tick.Request.ProgressSignalCount +
                            "; lastProgress=" + (tick.Request.LastProgressReason ?? "<none>") +
                            "; ackAccepted=" + tick.Request.AckAccepted +
                            "; baselineMsgs=" + tick.Request.BaselineMessageCount +
                            "; ramMsgs=" + host.GetStoredMessageCount() +
                            "; banner=" + host.IsHistoryCatchUpBannerActive))
                    .ConfigureAwait(false);

                if (exit.Outcome == HistoryCatchUpWatchdogOutcome.RequestCleared)
                {
                    host.WriteCatchUpWatchdogJournal(
                        "catch-up-watchdog-exit",
                        "requestId=" + state.RequestId + "; reason=request-cleared");
                    return;
                }

                if (exit.Outcome != HistoryCatchUpWatchdogOutcome.Idle)
                {
                    return;
                }

                HistoryOnDemandRequest last = exit.LastTick.Request;
                host.NotifyCatchUpIdleBatch(
                    state.RequestId,
                    last.AckAccepted,
                    last.ProgressSignalCount,
                    last.TriggerReason ?? "unspecified",
                    (int)exit.LastTick.Idle.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                host.LogCatchUpWatchdog(
                    "FullHistory catch-up watchdog failed: " + ex.Message);
                host.RecordCatchUpWatchdogException(ex, state.RequestId);
            }
        }
    }
}
