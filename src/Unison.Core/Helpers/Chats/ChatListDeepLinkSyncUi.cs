// =============================================================================
// ChatListDeepLinkSyncUi
//
// Toast / tile activation may hold the chat list until the conversation pane is
// ready — but only on cold start (app was not already open). Warm activation
// (process Running/Suspended, list already painted) must not arm the hold or
// suppress sync UI: just open the chat.
// =============================================================================
namespace Unison.Core.Helpers
{
    public static class ChatListDeepLinkSyncUi
    {
        /// <summary>
        /// True when toast/tile should delay chat-list materialize until the
        /// conversation loads. False when the app was already open.
        /// </summary>
        public static bool ShouldArmListHold(bool appWasAlreadyOpen)
        {
            return !appWasAlreadyOpen;
        }

        /// <summary>
        /// True when SyncStatusPanel / status-bar sync line must stay hidden
        /// (cold deep-link hold still loading the conversation).
        /// </summary>
        public static bool ShouldSuppressSyncBanner(bool deepLinkHeld)
        {
            return deepLinkHeld;
        }

        /// <summary>
        /// True when the full-list overlay must keep ChatList_Loading text instead
        /// of SyncProgress / HistoryCatchUp copy. Cold toast only.
        /// </summary>
        public static bool ShouldPreferLoadingOverlay(
            bool deepLinkHeld,
            bool listAlreadyMaterialized)
        {
            return deepLinkHeld && !listAlreadyMaterialized;
        }
    }
}
