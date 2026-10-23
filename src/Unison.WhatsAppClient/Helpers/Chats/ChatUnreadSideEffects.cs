// =============================================================================
// ChatUnreadSideEffects
//
// Badge, pinned-tile unread, and a catalogue slice after unread rows change.
// Shared by the local clear path (ChatFacade) and the app-state read path so
// the two directions stay the same policy.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Helpers
{
    public static class ChatUnreadSideEffects
    {
        /// <summary>
        /// Applies the three side effects that follow an unread mutation on one conversation.
        /// </summary>
        /// <param name="totalUnread">Badge total across every conversation.</param>
        /// <param name="canonicalJid">Conversation whose shortcut tile should be updated.</param>
        /// <param name="chatUnread">Unread count to show on that tile (0 after a local clear).</param>
        /// <param name="dirtyRows">Rows to rewrite; null or empty skips the slice persist.</param>
        public static void AfterConversationUnreadChanged(
            int totalUnread,
            string canonicalJid,
            int chatUnread,
            IList<ChatItem> dirtyRows,
            Action<int> updateBadge,
            Action<string, int> updateShortcutUnread,
            Action<IList<ChatItem>> persistRows)
        {
            updateBadge?.Invoke(totalUnread);

            if (!string.IsNullOrWhiteSpace(canonicalJid) && updateShortcutUnread != null)
            {
                try
                {
                    updateShortcutUnread(canonicalJid, chatUnread);
                }
                catch (Exception)
                {
                    // Shortcut updates are best-effort; a failed tile must not undo the row write.
                }
            }

            if (dirtyRows == null || dirtyRows.Count == 0 || persistRows == null)
            {
                return;
            }

            persistRows(dirtyRows);
        }
    }
}
