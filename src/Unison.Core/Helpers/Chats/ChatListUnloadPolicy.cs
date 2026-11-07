// =============================================================================
// ChatListUnloadPolicy
//
// ListView Unloaded runs before the page Detach. Leaving 250+ painted rows in
// VisibleChats forces UWP to tear down realized containers on the UI thread
// (cfb6fa4f: ~133s active=none after ClearRecordingUi on resume navigation).
// =============================================================================

namespace Unison.Core.Helpers.Chats
{
    public static class ChatListUnloadPolicy
    {
        /// <summary>
        /// Always drop painted rows before ListView teardown. Store catalogue stays in
        /// <c>ChatStateStore</c>; Attach rebuilds VisibleChats.
        /// </summary>
        public static bool ShouldClearVisibleChats(int visibleCount) =>
            visibleCount > 0;
    }
}
