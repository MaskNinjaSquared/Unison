using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Cross-platform chat shortcuts (Win SecondaryTile / Android widget / iOS shortcut).
    /// Win implementation pins a Start tile with contact photo + name, adaptive peek, and badge unread.
    /// </summary>
    public interface IShortcutService
    {
        /// <summary>Pins (or refreshes) a Start shortcut for the chat. Personal and group chats allowed.</summary>
        Task<bool> PinChatAsync(ChatItem chat);

        /// <summary>Removes the Start shortcut when present.</summary>
        Task<bool> UnpinChatAsync(string chatJid);

        /// <summary>True when a Start shortcut exists for this chat.</summary>
        Task<bool> IsChatPinnedAsync(string chatJid);

        /// <summary>
        /// Updates badge unread for a pinned chat shortcut only (no-op when not pinned).
        /// Call from notifications and when the chat is opened (cleared unread).
        /// </summary>
        void UpdateChatUnread(string chatJid, int unreadCount);

        /// <summary>
        /// Updates badge + adaptive live tile (circular peek avatar + preview) for a pinned chat.
        /// No-op when not pinned.
        /// </summary>
        void UpdatePinnedChatTile(
            string chatJid,
            int unreadCount,
            string title,
            string preview,
            string avatarUrl);

        /// <summary>
        /// Reconciles badges/live content for all existing pinned chat shortcuts from current chat state
        /// (call on app launch after chats load).
        /// </summary>
        Task RefreshPinnedUnreadAsync(IEnumerable<ChatItem> chats);
    }
}
