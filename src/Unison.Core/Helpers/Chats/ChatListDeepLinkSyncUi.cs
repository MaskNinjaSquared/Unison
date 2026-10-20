// =============================================================================
// ChatListDeepLinkSyncUi
//
// Toast / tile activation holds the chat list until the conversation pane is
// ready. While that hold is up, the list must not show "Synchronizing…" —
// only (on cold start) the delayed loading-chats overlay. After release, the
// host re-presents any pending sync banner. Warm start already has a list:
// suppress the sync line without covering rows with the loading overlay.
// =============================================================================
namespace Unison.Core.Helpers
{
    public static class ChatListDeepLinkSyncUi
    {
        /// <summary>
        /// True when SyncStatusPanel / status-bar sync line must stay hidden
        /// (conversation deep-link still loading).
        /// </summary>
        public static bool ShouldSuppressSyncBanner(bool deepLinkHeld)
        {
            return deepLinkHeld;
        }

        /// <summary>
        /// True when the full-list overlay must keep ChatList_Loading text instead
        /// of SyncProgress / HistoryCatchUp copy. Cold toast only — never cover an
        /// already-painted list on warm toast.
        /// </summary>
        public static bool ShouldPreferLoadingOverlay(
            bool deepLinkHeld,
            bool listAlreadyMaterialized)
        {
            return deepLinkHeld && !listAlreadyMaterialized;
        }
    }
}
