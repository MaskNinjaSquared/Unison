using Proto;
using Unison.Core.Models;
using Unison.Core.Helpers.Chats;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class HistorySyncNoticeTests
    {
        [Fact]
        public void On_demand_proto_type_is_still_recognised_by_the_list()
        {
            var notice = new HistorySyncNotice(HistorySync.Types.HistorySyncType.OnDemand.ToString());

            Assert.True(HistoryListUiRefresh.IsOnDemandSyncType(notice.SyncType));
        }

        [Fact]
        public void Bootstrap_proto_type_is_not_on_demand()
        {
            var notice = new HistorySyncNotice(HistorySync.Types.HistorySyncType.InitialBootstrap.ToString());

            Assert.False(HistoryListUiRefresh.IsOnDemandSyncType(notice.SyncType));
        }

        [Fact]
        public void Null_sync_type_becomes_empty()
        {
            Assert.Equal(string.Empty, new HistorySyncNotice(null).SyncType);
        }
    }
}
