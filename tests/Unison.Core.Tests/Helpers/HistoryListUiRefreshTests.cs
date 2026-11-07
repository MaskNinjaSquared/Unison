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
    }
}
