// =============================================================================
// FirstSyncUiAvatarPolicy
//
// WhatsApp-style first sync: block UI → load chats without network photos →
// unlock UI → load photos. Disk hydrate is fine during the hold; CDN / USync
// picture fetches wait until the list hold (and initial-sync safe mode) clear.
// =============================================================================

namespace Unison.Core.Helpers
{
    public static class FirstSyncUiAvatarPolicy
    {
        public const string DeferReason = "first-sync-ui-hold";

        /// <summary>
        /// True when network avatar fetches must wait for first-sync UI unlock.
        /// </summary>
        public static bool ShouldDeferNetworkAvatarFetch(
            bool firstSyncListHold,
            bool initialSyncActive)
        {
            return firstSyncListHold || initialSyncActive;
        }
    }
}
