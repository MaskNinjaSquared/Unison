// =============================================================================
// Tests for ChatListEnrichmentRefresh.
// =============================================================================
using Unison.Core.Helpers.Chats;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ChatListEnrichmentRefreshTests
    {
        [Theory]
        [InlineData(true, true, ChatListScope.Active, true)]
        [InlineData(true, true, ChatListScope.Channels, false)]
        [InlineData(true, true, ChatListScope.Archived, false)]
        [InlineData(true, false, ChatListScope.Active, false)]
        [InlineData(false, true, ChatListScope.Active, false)]
        public void Defer_only_when_frugal_enriching_and_active_scope(
            bool preferFrugal,
            bool enrichmentPhase,
            ChatListScope scope,
            bool expected)
        {
            Assert.Equal(
                expected,
                ChatListEnrichmentRefresh.ShouldDeferVisibleRefresh(
                    preferFrugal,
                    enrichmentPhase,
                    scope));
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

        [Theory]
        [InlineData(true, false, false, ChatListScope.Active, true)]
        [InlineData(false, true, true, ChatListScope.Active, true)]
        [InlineData(false, true, true, ChatListScope.Channels, false)]
        [InlineData(false, false, true, ChatListScope.Active, false)]
        public void Pins_defer_when_attach_already_deferred_or_frugal_enriching_active(
            bool alreadyDeferred,
            bool preferFrugal,
            bool enrichmentPhase,
            ChatListScope scope,
            bool expected)
        {
            Assert.Equal(
                expected,
                ChatListEnrichmentRefresh.ShouldDeferPinsVisibleRefresh(
                    alreadyDeferred,
                    preferFrugal,
                    enrichmentPhase,
                    scope));
        }

        [Theory]
        [InlineData(0, false)]
        [InlineData(1, true)]
        [InlineData(20, true)]
        public void Painted_list_prefers_in_place_reorder_after_pins(
            int visibleCount,
            bool expected)
        {
            Assert.Equal(
                expected,
                ChatListEnrichmentRefresh.PreferInPlaceReorderAfterPins(visibleCount));
        }

        [Theory]
        [InlineData(0, true, true)]
        [InlineData(0, false, false)]
        [InlineData(20, true, false)]
        [InlineData(1, true, false)]
        public void Empty_deferred_list_shows_loading_while_waiting_enrichment(
            int visibleCount,
            bool deferred,
            bool expected)
        {
            Assert.Equal(
                expected,
                ChatListEnrichmentRefresh.ShouldShowLoadingWhileDeferredEmpty(
                    visibleCount,
                    deferred));
        }

        [Theory]
        [InlineData(0, true)]
        [InlineData(1, false)]
        [InlineData(20, false)]
        public void Empty_list_gets_one_shot_paint_during_enrichment_deferral(
            int visibleCount,
            bool expected)
        {
            Assert.Equal(
                expected,
                ChatListEnrichmentRefresh.ShouldPaintEmptyListDuringEnrichmentDeferral(
                    visibleCount));
        }
    }
}
