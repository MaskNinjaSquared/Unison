using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class HistorySqliteChunkClassifyTests
    {
        [Theory]
        [InlineData("FullHistorySync", false, true)]
        [InlineData("HistorySyncOnDemand", true, false)]
        [InlineData("FullHistorySyncOnDemand", true, true)]
        [InlineData(null, false, false)]
        public void Classifies_on_demand_and_full(string syncType, bool onDemand, bool full)
        {
            HistorySqliteChunkClassify.Classify(syncType, out _, out bool isOnDemand, out bool isFull);
            Assert.Equal(onDemand, isOnDemand);
            Assert.Equal(full, isFull);
        }

        [Fact]
        public void Progress_and_release_rules_match_legacy()
        {
            Assert.Equal(5, HistorySqliteChunkClassify.ProgressTotal(true, 5));
            Assert.Equal(0, HistorySqliteChunkClassify.ProgressTotal(false, 5));
            Assert.Equal("sqlite-full", HistorySqliteChunkClassify.AppliedProgressReason(true));
            Assert.Equal("sqlite-chunk", HistorySqliteChunkClassify.AppliedProgressReason(false));
            Assert.True(HistorySqliteChunkClassify.ShouldCompleteUserResync(0, true));
            Assert.False(HistorySqliteChunkClassify.ShouldCompleteUserResync(0, false));
            Assert.True(HistorySqliteChunkClassify.ShouldReleaseEnrichment(true, false, 0));
            Assert.True(HistorySqliteChunkClassify.ShouldReleaseEnrichment(false, true, 2));
            Assert.False(HistorySqliteChunkClassify.ShouldReleaseEnrichment(false, true, 0));
            Assert.Equal("sqlite-starting-empty", HistorySqliteChunkClassify.StartingProgressReason(0));
        }
    }
}
