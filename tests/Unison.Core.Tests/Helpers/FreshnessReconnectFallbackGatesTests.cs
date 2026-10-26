using System;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class FreshnessReconnectFallbackGatesTests
    {
        [Fact]
        public void Starts_reconnect_when_stale_and_idle()
        {
            Assert.Equal(
                FreshnessReconnectScheduleAction.StartReconnect,
                FreshnessReconnectFallbackGates.DecideSchedule(
                    false,
                    false,
                    true,
                    DateTime.MinValue,
                    DateTime.UtcNow,
                    FreshnessReconnectFallbackGates.DefaultCooldown,
                    true));
        }

        [Fact]
        public void Hydrates_when_cooldown_active()
        {
            DateTime now = DateTime.UtcNow;
            Assert.Equal(
                FreshnessReconnectScheduleAction.HydrateOnCooldown,
                FreshnessReconnectFallbackGates.DecideSchedule(
                    false,
                    false,
                    true,
                    now - TimeSpan.FromMinutes(5),
                    now,
                    FreshnessReconnectFallbackGates.DefaultCooldown,
                    true));
        }

        [Theory]
        [InlineData(true, false, true, true, FreshnessReconnectScheduleAction.SkipInProgress)]
        [InlineData(false, true, true, true, FreshnessReconnectScheduleAction.SkipSuppress)]
        [InlineData(false, false, false, true, FreshnessReconnectScheduleAction.SkipNotRegistered)]
        [InlineData(false, false, true, false, FreshnessReconnectScheduleAction.SkipFresh)]
        public void Skips_when_blocked(
            bool inProgress,
            bool suppress,
            bool registered,
            bool stale,
            FreshnessReconnectScheduleAction expected)
        {
            Assert.Equal(
                expected,
                FreshnessReconnectFallbackGates.DecideSchedule(
                    inProgress,
                    suppress,
                    registered,
                    DateTime.MinValue,
                    DateTime.UtcNow,
                    FreshnessReconnectFallbackGates.DefaultCooldown,
                    stale));
        }

        [Fact]
        public void Aborts_after_delay_when_fresh_or_suppressed()
        {
            Assert.True(
                FreshnessReconnectFallbackGates.ShouldAbortAfterDelay(true, true, true));
            Assert.True(
                FreshnessReconnectFallbackGates.ShouldAbortAfterDelay(false, false, true));
            Assert.True(
                FreshnessReconnectFallbackGates.ShouldAbortAfterDelay(false, true, false));
            Assert.False(
                FreshnessReconnectFallbackGates.ShouldAbortAfterDelay(false, true, true));
        }
    }
}
