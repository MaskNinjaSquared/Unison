// =============================================================================
// Tests for app-state mute / pin mapping vs Baileys 7.0.0-rc14.
// =============================================================================
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class AppStateMutePinMappingTests
    {
        [Fact]
        public void Unmute_is_null_not_zero()
        {
            AppStateMuteMapping.Result result = AppStateMuteMapping.FromAction(
                muted: false,
                muteEndTimestamp: 0);

            Assert.True(result.AppliesMute);
            Assert.Null(result.MutedUntilUnixSeconds);
        }

        [Fact]
        public void Forever_mute_stays_zero()
        {
            AppStateMuteMapping.Result result = AppStateMuteMapping.FromAction(
                muted: true,
                muteEndTimestamp: 0);

            Assert.True(result.AppliesMute);
            Assert.Equal(0, result.MutedUntilUnixSeconds);
        }

        [Fact]
        public void Millisecond_deadlines_become_unix_seconds()
        {
            long ms = 1_700_000_000_000L;
            AppStateMuteMapping.Result result = AppStateMuteMapping.FromAction(
                muted: true,
                muteEndTimestamp: ms);

            Assert.Equal(1_700_000_000L, result.MutedUntilUnixSeconds);
        }

        [Fact]
        public void Second_deadlines_pass_through()
        {
            AppStateMuteMapping.Result result = AppStateMuteMapping.FromAction(
                muted: true,
                muteEndTimestamp: 1_700_000_000L);

            Assert.Equal(1_700_000_000L, result.MutedUntilUnixSeconds);
        }

        [Fact]
        public void Pin_with_missing_timestamp_is_still_pinned()
        {
            // SyncActionValue.timestamp defaults to 0 when absent. Collapsing that into
            // Pinned=0 made the host treat a real pin as an unpin (Pinned.Value > 0).
            AppStatePinMapping.Result result = AppStatePinMapping.FromAction(
                pinned: true,
                actionTimestamp: 0,
                fallbackTimestampMs: 1_700_000_000_123L);

            Assert.True(result.AppliesPin);
            Assert.True(result.IsPinned);
            Assert.Equal(1_700_000_000L, result.PinnedTimestampUnixSeconds);
        }

        [Fact]
        public void Pin_with_ms_timestamp_normalizes_to_seconds()
        {
            AppStatePinMapping.Result result = AppStatePinMapping.FromAction(
                pinned: true,
                actionTimestamp: 1_700_000_000_000L,
                fallbackTimestampMs: 99);

            Assert.True(result.IsPinned);
            Assert.Equal(1_700_000_000L, result.PinnedTimestampUnixSeconds);
        }

        [Fact]
        public void Unpin_clears_the_sort_key()
        {
            AppStatePinMapping.Result result = AppStatePinMapping.FromAction(
                pinned: false,
                actionTimestamp: 1_700_000_000L,
                fallbackTimestampMs: 99);

            Assert.True(result.AppliesPin);
            Assert.False(result.IsPinned);
            Assert.Equal(0, result.PinnedTimestampUnixSeconds);
        }

        [Fact]
        public void Pin_timestamp_in_seconds_passes_through()
        {
            Assert.Equal(
                1_700_000_000L,
                AppStatePinMapping.NormalizeSortKey(1_700_000_000L));
        }
    }
}
