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
            // Baileys processSyncAction: muted=false → muteEndTime: null.
            // Our ChatMuteHelper treats 0 as forever — emitting 0 for unmute made the icon stick.
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
            // Wire / SyncActionValue timestamps are usually ms; ChatItem.MutedUntil is seconds.
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
        public void Pin_timestamp_in_ms_normalizes_to_seconds_for_sort()
        {
            // History Conversation.pinned is uint32 seconds; SyncActionValue.timestamp is ms.
            // Mixing them left app-state pins always sorting above history pins.
            Assert.Equal(
                1_700_000_000L,
                AppStatePinMapping.NormalizeSortKey(1_700_000_000_000L));
        }

        [Fact]
        public void Pin_timestamp_in_seconds_passes_through()
        {
            Assert.Equal(
                1_700_000_000L,
                AppStatePinMapping.NormalizeSortKey(1_700_000_000L));
        }

        [Fact]
        public void Unpin_sort_key_is_zero()
        {
            Assert.Equal(0, AppStatePinMapping.NormalizeSortKey(0));
            Assert.Null(AppStatePinMapping.NormalizeSortKey(null));
        }
    }
}
