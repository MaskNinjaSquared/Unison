// =============================================================================
// ChatListSyncHold
//
// Whether the chat list must stay unmaterialized and show only the progress
// ring. First history sync of the account only, on every platform: the list is
// loaded once, after the bulk SQLite write, instead of fighting it row by row.
// Later bursts (reconnect, catch-up) are small enough to release progressively.
//
// The hold starts before the first chunk: app-state (mute, pin, archive)
// creates rows right after login, seconds before the initial-sync state turns
// on. Waiting for that state let those few rows show up first.
// =============================================================================
namespace Unison.Core.Helpers
{
    public static class ChatListSyncHold
    {
        public static bool ShouldHoldRows(bool isAwaitingFirstHistorySync)
        {
            return isAwaitingFirstHistorySync;
        }
    }
}
