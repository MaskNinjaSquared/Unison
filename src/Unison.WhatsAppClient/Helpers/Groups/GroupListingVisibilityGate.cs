// =============================================================================
// GroupListingVisibilityGate
//
// Pure visibility rules for participating-groups listing. 1.8 aborted enrichment
// phases when the window hid, but QueryAllGroups already in flight kept burning
// IQs under location-keepalive. Start and mid-pass abort both defer here.
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
        /// Stop unresolved metadata IQs and leave a retry armed once the window
        /// hides mid-pass (or an abort flag was raised from visibility Changed).
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
