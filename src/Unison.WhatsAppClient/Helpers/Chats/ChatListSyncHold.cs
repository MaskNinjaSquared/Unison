// =============================================================================
// ChatListSyncHold
//
// Whether the chat list must stay unmaterialized and show only the progress
// ring. First history sync of the account: the list is loaded once after the
// bulk SQLite write. Toast/tile deep-link: conversation loads first, then the
// list (x:Load) — but never tear down a ListView that already painted (warm
// start). Later bursts (reconnect, catch-up) release progressively.
//
// The first-sync hold starts before the first chunk: app-state (mute, pin,
// archive) creates rows right after login, seconds before the initial-sync
// state turns on. Waiting for that state let those few rows show up first.
// =============================================================================
namespace Unison.WhatsAppClient.Helpers
{
    public static class ChatListSyncHold
    {
        public static bool ShouldHoldRows(bool isAwaitingFirstHistorySync)
        {
            return ShouldHoldRows(
                isAwaitingFirstHistorySync,
                deepLinkDetailFirst: false,
                listAlreadyMaterialized: false);
        }

        /// <summary>
        /// True when the chat-list control must not be created yet (x:Load = false).
        /// </summary>
        public static bool ShouldHoldRows(
            bool isAwaitingFirstHistorySync,
            bool deepLinkDetailFirst,
            bool listAlreadyMaterialized)
        {
            if (isAwaitingFirstHistorySync)
            {
                return true;
            }

            // Cold toast: delay first materialize until the conversation is ready.
            // Warm toast: list already exists — do not unload it.
            if (deepLinkDetailFirst && !listAlreadyMaterialized)
            {
                return true;
            }

            return false;
        }
    }
}
