using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Cross-platform chat shortcuts (Win SecondaryTile / Android widget / iOS shortcut).
    /// Win: chat Start tiles open the conversation (<c>chat=</c>); contact Start tiles open Profile (<c>profile=</c>).
    /// </summary>
    public interface IShortcutService
    {
        /// <summary>Pins (or refreshes) a Start shortcut for the chat. Opens the conversation on tap.</summary>
        Task<bool> PinChatAsync(ChatItem chat);

        /// <summary>Removes the chat Start shortcut when present.</summary>
        Task<bool> UnpinChatAsync(string chatJid);

        /// <summary>
        /// Pins (or refreshes) a Start shortcut for the contact profile.
        /// Opens the full-page Profile on tap (<c>profile=</c>).
        /// </summary>
        Task<bool> PinContactAsync(ChatItem chat);

        /// <summary>Removes the contact Profile Start shortcut when present.</summary>
        Task<bool> UnpinContactAsync(string chatJid);

        /// <summary>
        /// Removes every Start shortcut this app created. Each removal asks the system,
        /// so a tile the user keeps is left in place.
        /// </summary>
        Task UnpinAllChatsAsync();

        /// <summary>True when a chat Start shortcut exists.</summary>
        Task<bool> IsChatPinnedAsync(string chatJid);

        /// <summary>True when a contact Profile Start shortcut exists.</summary>
        Task<bool> IsContactPinnedAsync(string chatJid);

        /// <summary>
        /// Updates badge unread for a pinned chat shortcut only (no-op when not pinned).
        /// Call from notifications and when the chat is opened (cleared unread).
        /// </summary>
        void UpdateChatUnread(string chatJid, int unreadCount);

        /// <summary>
        /// Updates badge + adaptive live tile for a pinned chat Start tile.
        /// Also refreshes a pinned contact Profile tile for the same JID when present.
        /// </summary>
        void UpdatePinnedChatTile(
            string chatJid,
            int unreadCount,
            string title,
            string preview,
            string avatarUrl);

        /// <summary>
        /// Reconciles badges/live content for all existing pinned chat and contact shortcuts
        /// (call on app launch after chats load).
        /// </summary>
        Task RefreshPinnedUnreadAsync(IEnumerable<ChatItem> chats);
    }
}
