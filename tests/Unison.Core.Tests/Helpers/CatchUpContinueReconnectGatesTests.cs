using System;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class CatchUpContinueReconnectGatesTests
    {
        [Fact]
        public void Starts_when_idle_and_registered()
        {
            Assert.Equal(
                CatchUpContinueScheduleAction.StartReconnect,
                CatchUpContinueReconnectGates.DecideSchedule(false, false, true));
        }

        [Theory]
        [InlineData(true, false, true, CatchUpContinueScheduleAction.FinishBusy)]
        [InlineData(false, true, true, CatchUpContinueScheduleAction.FinishSuppress)]
        [InlineData(false, false, false, CatchUpContinueScheduleAction.FinishUnregistered)]
        public void Finishes_when_blocked(
            bool busy,
            bool suppress,
            bool registered,
            CatchUpContinueScheduleAction expected)
        {
            Assert.Equal(
                expected,
                CatchUpContinueReconnectGates.DecideSchedule(busy, suppress, registered));
        }

        [Fact]
        public void Cooldown_is_within_window_only()
        {
            DateTime now = DateTime.UtcNow;
            Assert.True(
                CatchUpContinueReconnectGates.IsWithinCooldown(
                    now - TimeSpan.FromSeconds(5),
                    now,
                    CatchUpContinueReconnectGates.DefaultCooldown));
            Assert.False(
                CatchUpContinueReconnectGates.IsWithinCooldown(
                    DateTime.MinValue,
                    now,
                    CatchUpContinueReconnectGates.DefaultCooldown));
        }

        [Fact]
        public void Abort_after_delay_keeps_mid_catch_up_even_when_tip_fresh()
        {
            Assert.False(
                CatchUpContinueReconnectGates.ShouldAbortAfterDelay(
                    false, true, tipLooksFresh: true, catchUpActive: true));
            Assert.True(
                CatchUpContinueReconnectGates.ShouldAbortAfterDelay(
                    false, true, tipLooksFresh: true, catchUpActive: false));
            Assert.Equal(
                "fresh-before-continue",
                CatchUpContinueReconnectGates.AbortFinishReason(
                    false, true, tipLooksFresh: true, catchUpActive: false));
        }
    }
}
