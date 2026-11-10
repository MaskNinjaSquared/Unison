// =============================================================================
// Tests for HistoryListUiRefresh.
// =============================================================================
using Unison.Core.Helpers.Chats;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class HistoryListUiRefreshTests
    {
        [Theory]
        [InlineData("OnDemand", true)]
        [InlineData("HistorySyncType.OnDemand", true)]
        [InlineData("Full", false)]
        [InlineData("", false)]
        public void Detects_on_demand_sync_type(string syncType, bool expected)
        {
            Assert.Equal(expected, HistoryListUiRefresh.IsOnDemandSyncType(syncType));
        }

        [Fact]
        public void Null_sync_type_is_not_on_demand()
        {
            Assert.False(HistoryListUiRefresh.IsOnDemandSyncType(null));
        }

        [Fact]
        public void OnDemand_skips_visible_refresh()
        {
            Assert.False(HistoryListUiRefresh.ShouldRefreshVisibleChatsOnHistorySync(syncIsOnDemand: true));
            Assert.True(HistoryListUiRefresh.ShouldRefreshVisibleChatsOnHistorySync(syncIsOnDemand: false));
        }

        [Fact]
        public void OnDemand_skips_null_history_sync_received()
        {
            Assert.False(HistoryListUiRefresh.ShouldRaiseNullHistorySyncReceived(syncIsOnDemand: true));
            Assert.True(HistoryListUiRefresh.ShouldRaiseNullHistorySyncReceived(syncIsOnDemand: false));
        }

        [Fact]
        public void Tip_hydrate_skips_reorder_while_timeline_held()
        {
            Assert.False(HistoryListUiRefresh.ShouldReorderVisibleOnTipHydrate(timelineLoadPriorityHeld: true));
            Assert.True(HistoryListUiRefresh.ShouldReorderVisibleOnTipHydrate(timelineLoadPriorityHeld: false));
        }

        [Fact]
        public void OnDemand_skips_chat_messages_changed_notify()
        {
            Assert.False(
                HistoryListUiRefresh.ShouldNotifyChatMessagesChangedAfterChunk(syncIsOnDemand: true));
            Assert.True(
                HistoryListUiRefresh.ShouldNotifyChatMessagesChangedAfterChunk(syncIsOnDemand: false));
        }

        [Fact]
        public void Session_open_claim_allows_OnDemand_chat_messages_changed()
        {
            Assert.True(
                HistoryListUiRefresh.ShouldNotifyChatMessagesChangedForJid(
                    syncIsOnDemand: true,
                    sessionOpenOnDemandClaimed: true));
            Assert.False(
                HistoryListUiRefresh.ShouldNotifyChatMessagesChangedForJid(
                    syncIsOnDemand: true,
                    sessionOpenOnDemandClaimed: false));
            Assert.True(
                HistoryListUiRefresh.ShouldNotifyChatMessagesChangedForJid(
                    syncIsOnDemand: false,
                    sessionOpenOnDemandClaimed: false));
        }

        [Theory]
        [InlineData(0, 30, true)]
        [InlineData(1, 30, true)]
        [InlineData(29, 30, true)]
        [InlineData(30, 30, false)]
        [InlineData(1, 0, true)]
        [InlineData(2, 0, false)]
        public void Thin_timeline_replaces_on_history_sync(
            int visibleCount,
            int initialWindow,
            bool expected)
        {
            Assert.Equal(
                expected,
                HistoryListUiRefresh.ShouldReplaceTimelineOnHistorySync(visibleCount, initialWindow));
        }
    }
}
