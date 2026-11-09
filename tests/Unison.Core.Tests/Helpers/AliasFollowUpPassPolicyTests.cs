using System;
using Unison.Core.Helpers.Contacts;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class AliasFollowUpPassPolicyTests
    {
        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void ShouldRunDeduper_only_when_avatar_drain_done(
            bool hasMoreAvatarTargets,
            bool expectDeduper)
        {
            Assert.Equal(
                expectDeduper,
                AliasFollowUpPassPolicy.ShouldRunDeduper(hasMoreAvatarTargets));
        }

        [Theory]
        [InlineData(true, true)]
        [InlineData(false, false)]
        public void ShouldRearmAfterPass_only_with_pending_targets(
            bool hasPending,
            bool expectRearm)
        {
            Assert.Equal(
                expectRearm,
                AliasFollowUpPassPolicy.ShouldRearmAfterPass(hasPending));
        }

        [Theory]
        [InlineData(true, true, 2000)]
        [InlineData(true, false, 0)]
        [InlineData(false, true, 0)]
        public void RearmGapBeforeSchedule_spaces_frugal_more_passes(
            bool preferFrugal,
            bool hasMore,
            int expectMs)
        {
            Assert.Equal(
                TimeSpan.FromMilliseconds(expectMs),
                AliasFollowUpPassPolicy.RearmGapBeforeSchedule(preferFrugal, hasMore));
        }

        [Fact]
        public void RearmGapBeforeSchedule_defers_while_bulk_catalogue_mutates()
        {
            Assert.Equal(
                AliasFollowUpPassPolicy.BulkCatalogueBusyRearmDebounce,
                AliasFollowUpPassPolicy.RearmGapBeforeSchedule(
                    preferFrugal: false,
                    hasMoreAvatarTargets: false,
                    bulkCatalogueMutating: true));
            Assert.True(AliasFollowUpPassPolicy.ShouldDeferForBulkCatalogue(true));
            Assert.False(AliasFollowUpPassPolicy.ShouldDeferForBulkCatalogue(false));
        }
    }
}
