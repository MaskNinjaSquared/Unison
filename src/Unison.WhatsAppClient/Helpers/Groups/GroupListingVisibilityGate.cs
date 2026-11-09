// =============================================================================
// GroupListingVisibilityGate
//
// Pure visibility rules for participating-groups listing. 1.8 aborted enrichment
// phases when the window hid, but QueryAllGroups already in flight kept burning
// IQs under location-keepalive. Start and mid-pass abort both defer here.
//
// Subjects from a completed listing IQ are the exception: they must land in
// GroupSubjects even when the window hid (journal 82908f3f: after-listing-iq
// abort left hubs on "Comunidade"). Unresolved-metadata IQs stay gated; resume
// restamps hubs from the cache.
// =============================================================================

namespace Unison.WhatsAppClient.Helpers
{
    public static class GroupListingVisibilityGate
    {
        /// <summary>
        /// Do not arm <c>RunBackgroundListingAsync</c> / <c>QueryAllGroupsAsync</c>
        /// while CoreWindow is hidden (keepalive still holds the process).
        /// </summary>
        public static bool ShouldDeferStart(bool isWindowVisible) => !isWindowVisible;

        /// <summary>
        /// Listing IQ already answered — cache subjects even if the window hid.
        /// Only the heavier unresolved-metadata follow-up stays visibility-gated.
        /// </summary>
        public static bool ShouldApplyListingSubjects(bool hasListingResponse) =>
            hasListingResponse;

        /// <summary>
        /// Stop unresolved metadata IQs and leave a retry armed once the window
        /// hides mid-pass (or an abort flag was raised from visibility Changed).
        /// Does not block subject apply after a successful listing IQ.
        /// </summary>
        public static bool ShouldAbortInFlight(
            bool isWindowVisible,
            bool abortRequested) =>
            !isWindowVisible || abortRequested;

        /// <summary>
        /// After keepalive, listing retry must not arm on the same turn as
        /// <c>visible=True</c> (list refresh / fast-resume need the dispatcher first).
        /// Fire only when the settle generation still matches and the window stayed up.
        /// </summary>
        public static bool ShouldFireResumeRetry(
            bool isWindowVisible,
            bool listingRetryPending,
            int armedGeneration,
            int currentGeneration) =>
            isWindowVisible &&
            listingRetryPending &&
            armedGeneration == currentGeneration;
    }
}
