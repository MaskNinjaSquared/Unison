using System;
using System.Threading.Tasks;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class AcceptedHistoryRequestTimeoutDispatchTests
    {
        [Fact]
        public void Null_state_is_noop()
        {
            var host = new FakeHost();
            AcceptedHistoryRequestTimeoutDispatch.Schedule(host, null);
            Assert.False(host.FireAndForgetCalled);
        }

        [Fact]
        public void Arm_failure_skips_work()
        {
            var host = new FakeHost { ArmOk = false };
            var registry = new HistoryOnDemandRegistry();
            HistoryOnDemandRequest state = registry.RegisterFull(
                "r1",
                isRepair: false,
                baselineMessageCount: 0,
                reason: "t",
                nowUtc: DateTime.UtcNow);
            AcceptedHistoryRequestTimeoutDispatch.Schedule(host, state);
            Assert.False(host.FireAndForgetCalled);
        }

        [Fact]
        public void Full_history_arms_watchdog_and_fires()
        {
            var host = new FakeHost { ArmOk = true };
            var registry = new HistoryOnDemandRegistry();
            HistoryOnDemandRequest state = registry.RegisterFull(
                "r1",
                isRepair: false,
                baselineMessageCount: 0,
                reason: "t",
                nowUtc: DateTime.UtcNow);
            AcceptedHistoryRequestTimeoutDispatch.Schedule(host, state);
            Assert.True(host.WatchdogScheduled);
            Assert.True(host.FireAndForgetCalled);
        }

        [Fact]
        public async Task Expiry_notifies_catch_up_for_full_history()
        {
            var host = new FakeHost();
            var registry = new HistoryOnDemandRegistry();
            HistoryOnDemandRequest state = registry.RegisterFull(
                "r1",
                isRepair: false,
                baselineMessageCount: 1,
                reason: "t",
                nowUtc: DateTime.UtcNow);
            await AcceptedHistoryRequestTimeoutDispatch.RunOnExpiryAsync(
                host,
                state,
                Task.FromResult(state),
                isFullHistory: true,
                TimeSpan.FromSeconds(1));

            Assert.True(host.IdleBatchNotified);
        }

        private sealed class FakeHost : IAcceptedHistoryRequestTimeoutHost
        {
            public bool ArmOk;
            public bool FireAndForgetCalled;
            public bool WatchdogScheduled;
            public bool IdleBatchNotified;

            public bool TryArmHistoryRequestTimeout(
                HistoryOnDemandRequest state,
                out Task<HistoryOnDemandRequest> expiry)
            {
                expiry = Task.FromResult<HistoryOnDemandRequest>(null);
                return ArmOk;
            }

            public TimeSpan HistoryRequestTimeoutWindow(HistoryOnDemandRequest state) =>
                TimeSpan.FromSeconds(1);

            public void ScheduleFullHistoryCatchUpWatchdog(HistoryOnDemandRequest state) =>
                WatchdogScheduled = true;

            public int GetStoredMessageCount() => 0;

            public int GetChatMessageCount(string chatJid) => 0;

            public void LogHistoryRequestTimeout(string message)
            {
            }

            public void WriteCatchUpHardTimeoutJournal(
                string requestId,
                int progressSignals,
                string lastProgressReason,
                int baselineMessageCount,
                int currentMessageCount,
                string triggerReason)
            {
            }

            public void NotifyCatchUpIdleBatch(
                string requestId,
                bool ackAccepted,
                int progressSignals,
                string triggerReason,
                int idleMs) =>
                IdleBatchNotified = true;

            public void FireAndForgetHistoryRequestTimeout(Func<Task> work) =>
                FireAndForgetCalled = true;
        }
    }
}
