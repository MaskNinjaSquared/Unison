// =============================================================================
// Nested background-groups must not clear the Groups banner while in-flight.
// =============================================================================
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class GroupListingBannerOwnershipTests
    {
        [Theory]
        [InlineData(false, true)]
        [InlineData(true, false)]
        public void May_own_banner_only_when_listing_not_already_in_flight(
            bool listingAlreadyInFlight,
            bool expected)
        {
            Assert.Equal(
                expected,
                GroupListingBannerOwnership.MayOwnSyncBanner(listingAlreadyInFlight));
        }

        [Theory]
        [InlineData(false, true)]
        [InlineData(true, false)]
        public void Clear_banner_only_when_listing_no_longer_in_flight(
            bool listingStillInFlight,
            bool expected)
        {
            Assert.Equal(
                expected,
                GroupListingBannerOwnership.ShouldClearSyncBannerAfterPass(listingStillInFlight));
        }
    }
}
