using System;
using System.Threading.Tasks;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class FullHistoryCatchUpWatchdogDispatchTests
    {
        [Fact]
        public void Schedule_null_request_does_nothing()
        {
            var host = new FakeHost();
            FullHistoryCatchUpWatchdogDispatch.Schedule(host, null);
            Assert.False(host.FireAndForgetCalled);
        }

        [Fact]
        public void Schedule_fires_background_work()
        {
            var host = new FakeHost();
            HistoryOnDemandRequest state = new HistoryOnDemandRegistry().RegisterFull(
                "r1",
                isRepair: false,
                baselineMessageCount: 0,
                reason: "t",
                nowUtc: DateTime.UtcNow);
            FullHistoryCatchUpWatchdogDispatch.Schedule(host, state);
            Assert.True(host.FireAndForgetCalled);
        }

        [Fact]
        public async Task Run_idle_notifies_batch()
        {
            HistoryOnDemandRequest state = new HistoryOnDemandRegistry().RegisterFull(
                "r1",
                isRepair: false,
                baselineMessageCount: 0,
                reason: "t",
                nowUtc: DateTime.UtcNow);
            var host = new FakeHost
            {
                Exit = new HistoryCatchUpWatchdogExit
                {
                    Outcome = HistoryCatchUpWatchdogOutcome.Idle,
                    LastTick = new HistoryCatchUpWatchdogTick
                    {
                        Idle = TimeSpan.FromSeconds(30),
                        Request = state
                    }
                }
            };

            await FullHistoryCatchUpWatchdogDispatch.RunAsync(host, state);

            Assert.True(host.IdleNotified);
            Assert.Equal(30_000, host.IdleMs);
        }

        [Fact]
        public async Task Run_request_cleared_skips_idle_notify()
        {
            HistoryOnDemandRequest state = new HistoryOnDemandRegistry().RegisterFull(
                "r1",
                isRepair: false,
                baselineMessageCount: 0,
                reason: "t",
                nowUtc: DateTime.UtcNow);
            var host = new FakeHost
            {
                Exit = new HistoryCatchUpWatchdogExit
                {
                    Outcome = HistoryCatchUpWatchdogOutcome.RequestCleared
                }
            };

            await FullHistoryCatchUpWatchdogDispatch.RunAsync(host, state);

            Assert.False(host.IdleNotified);
            Assert.Contains("request-cleared", host.LastJournalDetail);
        }

        private sealed class FakeHost : IFullHistoryCatchUpWatchdogHost
        {
            public HistoryCatchUpWatchdogExit Exit;
            public bool FireAndForgetCalled;
            public bool IdleNotified;
            public int IdleMs;
            public string LastJournalDetail;

            public TimeSpan FullHistoryCatchUpIdleAbort => TimeSpan.FromMinutes(1);
            public TimeSpan FullHistoryCatchUpProgressPoll => TimeSpan.FromSeconds(1);
            public TimeSpan FullHistoryOnDemandResponseTimeout => TimeSpan.FromMinutes(5);
            public bool IsHistoryCatchUpBannerActive => false;

            public int GetStoredMessageCount() => 0;

            public void WriteCatchUpWatchdogJournal(string eventName, string detail) =>
                LastJournalDetail = detail;

            public Task<HistoryCatchUpWatchdogExit> WatchCatchUpAsync(
                HistoryOnDemandRequest state,
                Action<HistoryCatchUpWatchdogTick> onTick) =>
                Task.FromResult(Exit);

            public void NotifyCatchUpIdleBatch(
                string requestId,
                bool ackAccepted,
                int progressSignals,
                string triggerReason,
                int idleMs)
            {
                IdleNotified = true;
                IdleMs = idleMs;
            }

            public void LogCatchUpWatchdog(string message)
            {
            }

            public void RecordCatchUpWatchdogException(Exception error, string requestId)
            {
            }

            public void FireAndForgetCatchUpWatchdog(Func<Task> work) =>
                FireAndForgetCalled = true;
        }
    }
}
