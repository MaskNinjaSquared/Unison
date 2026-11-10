// =============================================================================
// SessionListingApplyPolicy
//
// Participating-groups listing UI apply once per session unless forced. A second
// background-groups pass after tip-fresh was stacking ApplyListingAsync on Mobile.
// =============================================================================

namespace Unison.Core.Helpers.Chats
{
    public static class SessionListingApplyPolicy
    {
        /// <summary>
        /// False when this session already applied a listing and the caller did not force.
        /// </summary>
        public static bool ShouldApplyListing(bool alreadyAppliedThisSession, bool force) =>
            force || !alreadyAppliedThisSession;
    }
}
