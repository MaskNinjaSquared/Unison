// =============================================================================
// NotificationAudience
//
// Whether a chat matches lock-screen ComboBox scope or toast allow-list filter.
// Channels are neither groups nor direct — only All / Favorites.
// =============================================================================
using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.Core.Helpers.Notifications
{
    public static class NotificationAudience
    {
        public static bool MatchesLockScreen(
            LockScreenNotificationScope scope,
            bool isGroup,
            bool isDirect,
            bool isFavorite,
            bool isChannel)
        {
            switch (scope)
            {
                case LockScreenNotificationScope.All:
                    return true;
                case LockScreenNotificationScope.Groups:
                    return isGroup && !isChannel;
                case LockScreenNotificationScope.Direct:
                    return isDirect && !isChannel;
                case LockScreenNotificationScope.Favorites:
                    return isFavorite;
                default:
                    return true;
            }
        }

        public static bool MatchesToastFilter(
            HashSet<string> allowed,
            bool isGroup,
            bool isDirect,
            bool isFavorite,
            bool isChannel)
        {
            if (allowed == null || allowed.Count == 0)
            {
                return false;
            }

            if (isFavorite &&
                NotificationFilterCodec.Contains(allowed, NotificationFilterCodec.TokenFavorites))
            {
                return true;
            }

            if (isChannel)
            {
                return false;
            }

            if (isGroup &&
                NotificationFilterCodec.Contains(allowed, NotificationFilterCodec.TokenGroups))
            {
                return true;
            }

            if (isDirect &&
                NotificationFilterCodec.Contains(allowed, NotificationFilterCodec.TokenDirect))
            {
                return true;
            }

            return false;
        }

        public static bool MatchesToastFilter(
            HashSet<string> allowed,
            ChatItem chat)
        {
            if (chat == null)
            {
                return false;
            }

            // Contatos = Direct 1:1 and Personal (Message yourself); not channels/communities.
            bool countsAsDirect = chat.IsDirect || chat.IsPersonal;
            return MatchesToastFilter(
                allowed,
                isGroup: chat.IsGroup,
                isDirect: countsAsDirect,
                isFavorite: chat.IsFavorite,
                isChannel: chat.IsChannel);
        }

        public static bool MatchesLockScreen(LockScreenNotificationScope scope, ChatItem chat)
        {
            if (chat == null)
            {
                return false;
            }

            bool countsAsDirect = chat.IsDirect || chat.IsPersonal;
            return MatchesLockScreen(
                scope,
                isGroup: chat.IsGroup,
                isDirect: countsAsDirect,
                isFavorite: chat.IsFavorite,
                isChannel: chat.IsChannel);
        }
    }
}
