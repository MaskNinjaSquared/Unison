using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ConnectionSoftReconnectGatesTests
    {
        [Fact]
        public void Schedule_starts_when_idle()
        {
            Assert.Equal(
                SoftReconnectScheduleAction.StartLoop,
                ConnectionSoftReconnectGates.DecideSchedule(false, false, false, false));
        }

        [Theory]
        [InlineData(true, false, false, false)]
        [InlineData(false, true, false, false)]
        [InlineData(false, false, true, false)]
        [InlineData(false, false, false, true)]
        public void Schedule_skips_when_busy_or_suppressed(
            bool suppress,
            bool fatal,
            bool reconnecting,
            bool pairing)
        {
            Assert.Equal(
                SoftReconnectScheduleAction.Skip,
                ConnectionSoftReconnectGates.DecideSchedule(suppress, fatal, reconnecting, pairing));
        }

        [Fact]
        public void PostLoop_reschedules_when_still_registered_and_down()
        {
            Assert.Equal(
                SoftReconnectPostLoopAction.Reschedule,
                ConnectionSoftReconnectGates.DecidePostLoop(
                    false, false, false, true, true, false));
        }

        [Fact]
        public void PostLoop_falls_back_to_pairing_when_pending()
        {
            Assert.Equal(
                SoftReconnectPostLoopAction.PairingStage2Fallback,
                ConnectionSoftReconnectGates.DecidePostLoop(
                    false, false, true, true, true, false));
        }

        [Fact]
        public void Loop_helpers_match_legacy_branches()
        {
            Assert.True(ConnectionSoftReconnectGates.ShouldContinueLoop(false, false));
            Assert.False(ConnectionSoftReconnectGates.ShouldContinueLoop(true, false));
            Assert.True(ConnectionSoftReconnectGates.ShouldExitForPairing(true));
            Assert.True(ConnectionSoftReconnectGates.ShouldStopBecauseSessionNotRegistered(false, false, false));
            Assert.True(ConnectionSoftReconnectGates.ShouldAbortAfterDelay(false, false, true));
            Assert.True(ConnectionSoftReconnectGates.ShouldSkipPairingStage2Start(true, false));
            Assert.True(ConnectionSoftReconnectGates.ShouldAbortPairingRestart(true, false));
            Assert.True(ConnectionSoftReconnectGates.CanScheduleSoftReconnect(false, false));
            Assert.False(ConnectionSoftReconnectGates.CanScheduleSoftReconnect(true, false));
            Assert.True(
                ConnectionSoftReconnectGates.ShouldRaiseReconnectLoopError(true, false, false));
            Assert.False(
                ConnectionSoftReconnectGates.ShouldRaiseReconnectLoopError(true, true, false));
            Assert.False(
                ConnectionSoftReconnectGates.ShouldRaiseReconnectLoopError(false, false, false));
        }
    }
}
