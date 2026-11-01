using System;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class SilentFullHistoryPolicyTests
    {
        [Theory]
        [InlineData(DeviceCategory.High, 2)]
        [InlineData(DeviceCategory.Low, 3)]
        [InlineData(DeviceCategory.Unsupported, 5)]
        public void Delay_matches_tier_minutes(DeviceCategory category, int minutes)
        {
            Assert.Equal(TimeSpan.FromMinutes(minutes), SilentFullHistoryPolicy.DelayFor(category));
        }

        [Fact]
        public void Banner_only_when_messages_were_applied()
        {
            Assert.False(SilentFullHistoryPolicy.ShouldShowBanner(0));
            Assert.True(SilentFullHistoryPolicy.ShouldShowBanner(1));
        }

        [Fact]
        public void After_a_silent_quiet_lot_schedules_the_next_probe()
        {
            SilentFullHistoryNext next = SilentFullHistoryPolicy.AfterQuietLot(
                lotWasSilent: true,
                fullClaimed: false,
                consecutiveEmptyAfterThisLot: 1,
                out string reason);
            Assert.Equal(SilentFullHistoryNext.ScheduleAfterDelay, next);
            Assert.Equal("schedule-next", reason);
        }

        [Fact]
        public void A_non_silent_or_still_claimed_lot_does_not_schedule()
        {
            Assert.Equal(
                SilentFullHistoryNext.None,
                SilentFullHistoryPolicy.AfterQuietLot(false, false, 1, out _));
            Assert.Equal(
                SilentFullHistoryNext.None,
                SilentFullHistoryPolicy.AfterQuietLot(true, true, 1, out _));
        }

        [Fact]
        public void Fourth_consecutive_empty_lot_reaches_cap()
        {
            SilentFullHistoryNext next = SilentFullHistoryPolicy.AfterQuietLot(
                lotWasSilent: true,
                fullClaimed: false,
                consecutiveEmptyAfterThisLot: SilentFullHistoryPolicy.MaxConsecutiveEmptyLots,
                out string reason);
            Assert.Equal(SilentFullHistoryNext.CapReached, next);
            Assert.Equal("empty-cap", reason);
        }

        [Fact]
        public void Empty_streak_just_under_cap_still_schedules()
        {
            SilentFullHistoryNext next = SilentFullHistoryPolicy.AfterQuietLot(
                lotWasSilent: true,
                fullClaimed: false,
                consecutiveEmptyAfterThisLot: SilentFullHistoryPolicy.MaxConsecutiveEmptyLots - 1,
                out string reason);
            Assert.Equal(SilentFullHistoryNext.ScheduleAfterDelay, next);
            Assert.Equal("schedule-next", reason);
        }

        [Fact]
        public void NextConsecutiveEmpty_resets_on_productive_lot()
        {
            Assert.Equal(0, SilentFullHistoryPolicy.NextConsecutiveEmpty(3, appliedCount: 2));
            Assert.Equal(1, SilentFullHistoryPolicy.NextConsecutiveEmpty(0, appliedCount: 0));
            Assert.Equal(4, SilentFullHistoryPolicy.NextConsecutiveEmpty(3, appliedCount: 0));
        }

        [Fact]
        public void CanFire_requires_socket_idle_claim_and_previous_quiet()
        {
            Assert.True(SilentFullHistoryPolicy.CanFire(true, false, false, true, out _));
            Assert.False(SilentFullHistoryPolicy.CanFire(false, false, false, true, out _));
            Assert.False(SilentFullHistoryPolicy.CanFire(true, false, false, false, out _));
            Assert.False(SilentFullHistoryPolicy.CanFire(true, true, false, true, out _));
            Assert.False(SilentFullHistoryPolicy.CanFire(true, false, true, true, out _));
        }

        [Fact]
        public void Silent_reason_prefix_is_recognized()
        {
            Assert.True(SilentFullHistoryPolicy.IsSilentReason("silent-full:post-init"));
            Assert.False(SilentFullHistoryPolicy.IsSilentReason("periodic-safety:tip-stale"));
            Assert.False(SilentFullHistoryPolicy.IsSilentReason(null));
        }
    }
}
