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
            string avatarUrl = null);

        /// <summary>Generic toast (e.g. session logged out). Respects notifications setting.</summary>
        void ShowToast(string title, string body);

        void RefreshUnreadState(IEnumerable<ChatItem> chats);
        void UpdateBadge(int totalUnread);
        void ClearAll();

        /// <summary>Called when the user toggles notifications in Settings.</summary>
        void OnNotificationsConfigChanged();
    }
}
