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

        [Fact]
        public void Toast_deep_link_holds_list_until_conversation_loads()
        {
            Assert.True(ChatListSyncHold.ShouldHoldRows(
                isAwaitingFirstHistorySync: false,
                deepLinkDetailFirst: true,
                listAlreadyMaterialized: false));
        }

        [Fact]
        public void Warm_toast_does_not_unload_an_existing_list()
        {
            Assert.False(ChatListSyncHold.ShouldHoldRows(
                isAwaitingFirstHistorySync: false,
                deepLinkDetailFirst: true,
                listAlreadyMaterialized: true));
        }

        [Fact]
        public void First_sync_hold_wins_even_if_list_had_painted_before_wipe()
        {
            Assert.True(ChatListSyncHold.ShouldHoldRows(
                isAwaitingFirstHistorySync: true,
                deepLinkDetailFirst: false,
                listAlreadyMaterialized: true));
        }
    }
}
