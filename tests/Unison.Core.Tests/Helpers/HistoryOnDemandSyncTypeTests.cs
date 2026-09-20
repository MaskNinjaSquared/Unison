// =============================================================================
// Tests for HistoryOnDemandSyncType.
// =============================================================================
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class HistoryOnDemandSyncTypeTests
    {
        [Fact]
        public void Null_or_empty_is_not_on_demand()
        {
            Assert.False(HistoryOnDemandSyncType.Matches(null));
            Assert.False(HistoryOnDemandSyncType.Matches(""));
        }

        [Theory]
        [InlineData("OnDemand")]
        [InlineData("FULL_OnDemand")]
        [InlineData("recent_onDemand")]
        [InlineData("HISTORY_ONDEMAND_SYNC")]
        public void On_demand_tokens_match(string syncType)
        {
            Assert.True(HistoryOnDemandSyncType.Matches(syncType));
        }

        [Theory]
        [InlineData("INITIAL_BOOTSTRAP")]
        [InlineData("FULL")]
        [InlineData("RECENT")]
        public void Bulk_sync_types_do_not_match(string syncType)
        {
            Assert.False(HistoryOnDemandSyncType.Matches(syncType));
        }
    }
}
