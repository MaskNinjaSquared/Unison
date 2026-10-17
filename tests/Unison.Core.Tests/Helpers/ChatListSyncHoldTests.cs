// =============================================================================
// Tests for ChatListSyncHold.
// =============================================================================
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ChatListSyncHoldTests
    {
        [Fact]
        public void Holds_rows_until_the_first_history_sync_finished()
        {
            // App-state (mute/pin) creates rows right after login, before the first history chunk
            // turns the initial-sync state on. Those must not leak onto the list.
            Assert.True(ChatListSyncHold.ShouldHoldRows(isAwaitingFirstHistorySync: true));
        }

        [Fact]
        public void Does_not_hold_rows_for_later_syncs()
        {
            // Reconnect / catch-up bursts release progressively.
            Assert.False(ChatListSyncHold.ShouldHoldRows(isAwaitingFirstHistorySync: false));
        }
    }
}
