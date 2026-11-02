using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Toast / badge surface. Platform-specific (WinRT) behind this contract.
    /// Live Tile updates go through <see cref="ILiveTilesService"/>.
    /// </summary>
    public interface INotificationService
    {
        void Initialize();

        void NotifyIncomingMessage(
            string chatJid,
            string chatName,
            string senderName,
            string preview,
            bool isGroup,
            bool isMuted,
            bool suppressToast,
            int totalUnread,
            string avatarUrl = null,
            int chatUnread = -1,
            bool isCommunityAnnounce = false,
            bool isFavorite = false,
            bool isChannel = false);

        /// <summary>Generic toast (e.g. session logged out). Respects notifications setting.</summary>
        void ShowToast(string title, string body);

        /// <summary>
        /// Call alert toast (Offer / Missed). Respects NotificationsEnabled + CallNotificationsEnabled.
        /// When <paramref name="updateLiveTile"/> is true (Missed), also pushes the primary Live Tile preview.
        /// </summary>
        void NotifyCallAlert(
            string title,
            string body,
            string chatJid = null,
            bool updateLiveTile = false,
            bool isFavorite = false);

        void RefreshUnreadState(IEnumerable<ChatItem> chats);
        void UpdateBadge(int totalUnread);
        void ClearAll();

        /// <summary>Called when the user toggles notifications in Settings.</summary>
        void OnNotificationsConfigChanged();
    }
}
