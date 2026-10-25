using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Connection;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class SoftReconnectCoordinatorTests
    {
        [Fact]
        public void Schedule_claims_slot_and_starts_loop_when_idle()
        {
            var io = new RecordingIoHost();
            var soft = new SoftReconnectCoordinator(io);

            soft.Schedule("test");

            Assert.True(soft.IsReconnecting);
            Assert.Equal(1, io.LoopStarts);
            Assert.Equal(1, io.LogScheduledCalls);
        }

        [Fact]
        public void Schedule_skips_when_already_reconnecting()
        {
            var io = new RecordingIoHost();
            var soft = new SoftReconnectCoordinator(io);

            Assert.True(soft.TryClaimReconnectSlot());
            soft.Schedule("second");

            Assert.True(soft.IsReconnecting);
            Assert.Equal(0, io.LoopStarts);
        }

        [Fact]
        public void Schedule_skips_when_pairing_pending_and_traces()
        {
            var io = new RecordingIoHost { PairingRestartPending = true };
            var soft = new SoftReconnectCoordinator(io);

            soft.Schedule("pairing-busy");

            Assert.False(soft.IsReconnecting);
            Assert.Equal(0, io.LoopStarts);
            Assert.Equal(1, io.PairingSkipTraces);
        }

        [Fact]
        public void TryClaimReconnectSlot_is_idempotent()
        {
            var soft = new SoftReconnectCoordinator(new RecordingIoHost());

            Assert.True(soft.TryClaimReconnectSlot());
            Assert.False(soft.TryClaimReconnectSlot());
            Assert.True(soft.IsReconnecting);

            soft.ClearReconnectingFlag();
            Assert.False(soft.IsReconnecting);
            Assert.True(soft.TryClaimReconnectSlot());
        }

        [Fact]
        public async Task Complete_clears_slot_and_can_reschedule()
        {
            var io = new RecordingIoHost
            {
                IsAuthPresent = true,
                IsAuthRegistered = true,
                IsConnected = false
            };
            var soft = new SoftReconnectCoordinator(io);

            soft.Schedule("first");
            Assert.Equal(1, io.LoopStarts);

            // Simulate dispatch finally: Complete then post-loop Reschedule.
            SoftReconnectPostLoopAction post =
                ((IScheduleAutoReconnectHost)soft).CompleteAutoReconnectLoop();
            Assert.Equal(SoftReconnectPostLoopAction.Reschedule, post);
            Assert.False(soft.IsReconnecting);

            soft.Schedule("post-loop");
            Assert.Equal(2, io.LoopStarts);
            Assert.True(soft.IsReconnecting);

            await Task.CompletedTask;
        }

        private sealed class RecordingIoHost : ISoftReconnectIoHost
        {
            public bool SuppressReconnect { get; set; }
            public bool FatalSessionEnded { get; set; }
            public bool PairingRestartPending { get; set; }
            public bool IsAuthPresent { get; set; }
            public bool IsAuthRegistered { get; set; }
            public bool IsConnected { get; set; } = true;

            public int LoopStarts { get; private set; }
            public int LogScheduledCalls { get; private set; }
            public int PairingSkipTraces { get; private set; }

            public void LogAutoReconnectScheduled(string reason) => LogScheduledCalls++;

            public void WriteAutoReconnectScheduled(string reason)
            {
            }

            public void TracePairingSkippedForPending(string reason) => PairingSkipTraces++;

            public void TraceScheduleAutoReconnectReason(string reason)
            {
            }

            public void FireAndForgetAutoReconnect(System.Func<Task> work)
            {
                LoopStarts++;
                // Do not run the loop body — claim/complete is what we test.
            }

            public Task RunAutoReconnectLoopAsync(string reason) => Task.CompletedTask;

            public void TryStartPairingStage2Reconnect(string reason)
            {
            }
        }
    }
}
