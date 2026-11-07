// =============================================================================
// Tests for ChatListEnrichmentRefresh.
// =============================================================================
using Unison.Core.Helpers.Chats;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ChatListEnrichmentRefreshTests
    {
        [Theory]
        [InlineData(true, true, true)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(false, false, false)]
        public void Defer_only_when_frugal_and_enriching(
            bool preferFrugal,
            bool enrichmentPhase,
            bool expected)
        {
            Assert.Equal(
                expected,
                ChatListEnrichmentRefresh.ShouldDeferVisibleRefresh(preferFrugal, enrichmentPhase));
        }

        [Theory]
        [InlineData(0, true, true)]
        [InlineData(0, false, false)]
        [InlineData(20, true, false)]
        [InlineData(1, true, false)]
        public void Empty_dirty_list_requires_full_refresh_after_enrichment(
            int visibleCount,
            bool orderDirty,
            bool expected)
        {
            Assert.Equal(
                expected,
                ChatListEnrichmentRefresh.RequiresFullRefreshAfterEnrichment(visibleCount, orderDirty));
        }
    }
}
