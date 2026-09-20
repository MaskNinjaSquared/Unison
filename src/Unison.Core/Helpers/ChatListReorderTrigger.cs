// =============================================================================
// ChatListReorderTrigger
//
// Which ChatItem property change can move a row in the list. Pin is the only
// thing that outranks recency, so a pin arriving from app-state mid-sync has to
// reopen the ordering question even though the row itself repaints on its own.
//
// Mute is deliberately absent: it changes the icon, never the position.
// =============================================================================
using System;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public static class ChatListReorderTrigger
    {
        public static bool RequiresVisibleReorder(string propertyName)
        {
            if (string.IsNullOrEmpty(propertyName))
            {
                return false;
            }

            return string.Equals(propertyName, nameof(ChatItem.IsChatPinned), StringComparison.Ordinal) ||
                   string.Equals(propertyName, nameof(ChatItem.PinnedTimestamp), StringComparison.Ordinal) ||
                   string.Equals(propertyName, nameof(ChatItem.Status), StringComparison.Ordinal);
        }
    }
}
