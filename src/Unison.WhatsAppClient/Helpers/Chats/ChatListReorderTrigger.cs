// =============================================================================
// ChatListReorderTrigger
//
// Which ChatItem property change can move a row in the list (or change who is
// nested under a community hub). Pin is the only thing that outranks recency;
// community link / parent flags change list membership via CommunityListProjector.
//
// Mute is deliberately absent: it changes the icon, never the position.
// =============================================================================
using System;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Helpers
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
                   string.Equals(propertyName, nameof(ChatItem.Status), StringComparison.Ordinal) ||
                   string.Equals(propertyName, nameof(ChatItem.LinkedParentJid), StringComparison.Ordinal) ||
                   string.Equals(propertyName, nameof(ChatItem.IsCommunityParent), StringComparison.Ordinal);
        }
    }
}
