using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ScheduleAutoReconnectDispatchTests
    {
        [Fact]
        public void Skip_does_not_fire_work()
        {
            var host = new FakeHost
            {
                Claim = SoftReconnectScheduleAction.Skip,
                SkippedForPairing = true
            };
            ScheduleAutoReconnectDispatch.Schedule(host, "t");
            Assert.False(host.FireAndForgetCalled);
            Assert.True(host.PairingSkipTraced);
        }

        [Fact]
        public void Start_claims_and_fires_loop()
        {
            var host = new FakeHost { Claim = SoftReconnectScheduleAction.StartLoop };
            ScheduleAutoReconnectDispatch.Schedule(host, "go");
            Assert.True(host.FireAndForgetCalled);
            Assert.True(host.Logged);
        }

        [Fact]
        public async Task Post_loop_reschedule()
        {
            var host = new FakeHost
            {
                PostLoop = SoftReconnectPostLoopAction.Reschedule
            };
            await ScheduleAutoReconnectDispatch.RunLoopAsync(host, "t");
            Assert.True(host.LoopRan);
            Assert.Equal("post-loop-disconnected", host.RescheduleReason);
        }

        [Fact]
        public async Task Post_loop_pairing_fallback()
        {
            var host = new FakeHost
            {
                PostLoop = SoftReconnectPostLoopAction.PairingStage2Fallback
            };
            await ScheduleAutoReconnectDispatch.RunLoopAsync(host, "t");
            Assert.True(host.PairingStage2Started);
        }

        private sealed class FakeHost : IScheduleAutoReconnectHost
        {
            public SoftReconnectScheduleAction Claim;
            public bool SkippedForPairing;
            public SoftReconnectPostLoopAction PostLoop;
            public bool FireAndForgetCalled;
            public bool Logged;
            public bool LoopRan;
            public string RescheduleReason;
            public bool PairingStage2Started;
            public bool PairingSkipTraced;

            public SoftReconnectScheduleAction TryClaimAutoReconnectSlot(
                out bool skippedForPairing)
            {
                skippedForPairing = SkippedForPairing;
                return Claim;
            }

            public void LogAutoReconnectScheduled(string reason) => Logged = true;

            public void WriteAutoReconnectScheduled(string reason)
            {
            }

            public void TracePairingSkippedForPending(string reason) =>
                PairingSkipTraced = true;

            public void TraceScheduleAutoReconnectReason(string reason)
            {
            }

            public void FireAndForgetAutoReconnect(Func<Task> work) =>
                FireAndForgetCalled = true;

            public Task RunAutoReconnectLoopAsync(string reason)
            {
                LoopRan = true;
                return Task.CompletedTask;
            }

            public SoftReconnectPostLoopAction CompleteAutoReconnectLoop() => PostLoop;

            public void ScheduleAutoReconnect(string reason) => RescheduleReason = reason;

            public void TryStartPairingStage2Reconnect(string reason) =>
                PairingStage2Started = true;
        }
    }
}
