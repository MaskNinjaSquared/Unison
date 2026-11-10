// =============================================================================
// GroupListingBannerOwnership
//
// Nested NotifyGroupListingDue while a pass is already in flight must not
// Raise(Groups) / Raise(null) — the clear released frugal chat-list deferral
// mid unresolved-metadata and cold-built Active for minutes (6707be8d).
// =============================================================================

namespace Unison.WhatsAppClient.Helpers
{
    public static class GroupListingBannerOwnership
    {
        /// <summary>
        /// True when this <c>RunBackgroundListingAsync</c> may advertise the Groups
        /// banner. False when another listing already owns the in-flight slot.
        /// </summary>
        public static bool MayOwnSyncBanner(bool listingAlreadyInFlight) =>
            !listingAlreadyInFlight;

        /// <summary>
        /// After a background pass returns: clear the banner only when nothing is
        /// still in flight (a raced loser must not Raise(null) over the winner).
        /// </summary>
        public static bool ShouldClearSyncBannerAfterPass(bool listingStillInFlight) =>
            !listingStillInFlight;
    }
}
