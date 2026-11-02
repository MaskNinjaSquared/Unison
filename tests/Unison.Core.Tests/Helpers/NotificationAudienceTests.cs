using System.Collections.Generic;
using Unison.Core.Helpers.Notifications;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class NotificationAudienceTests
    {
        private static HashSet<string> AllTokens() =>
            NotificationFilterCodec.Parse(NotificationFilterCodec.DefaultSerialized);

        [Fact]
        public void Lock_screen_all_accepts_any_chat()
        {
            Assert.True(NotificationAudience.MatchesLockScreen(
                LockScreenNotificationScope.All, true, false, false, false));
            Assert.True(NotificationAudience.MatchesLockScreen(
                LockScreenNotificationScope.All, false, false, false, true));
        }

        [Fact]
        public void Lock_screen_groups_and_direct_exclude_channels()
        {
            Assert.True(NotificationAudience.MatchesLockScreen(
                LockScreenNotificationScope.Groups, true, false, false, false));
            Assert.False(NotificationAudience.MatchesLockScreen(
                LockScreenNotificationScope.Groups, true, false, false, true));
            Assert.True(NotificationAudience.MatchesLockScreen(
                LockScreenNotificationScope.Direct, false, true, false, false));
            Assert.False(NotificationAudience.MatchesLockScreen(
                LockScreenNotificationScope.Direct, false, true, false, true));
        }

        [Fact]
        public void Lock_screen_favorites_requires_favorite_flag()
        {
            Assert.True(NotificationAudience.MatchesLockScreen(
                LockScreenNotificationScope.Favorites, false, false, true, true));
            Assert.False(NotificationAudience.MatchesLockScreen(
                LockScreenNotificationScope.Favorites, true, false, false, false));
        }

        [Fact]
        public void Toast_filter_default_allows_group_and_direct()
        {
            HashSet<string> all = AllTokens();
            Assert.True(NotificationAudience.MatchesToastFilter(all, true, false, false, false));
            Assert.True(NotificationAudience.MatchesToastFilter(all, false, true, false, false));
            Assert.False(NotificationAudience.MatchesToastFilter(all, false, false, false, true));
        }

        [Fact]
        public void Toast_filter_favorite_channel_when_favorites_enabled()
        {
            var favoritesOnly = NotificationFilterCodec.Parse("favorites");
            Assert.True(NotificationAudience.MatchesToastFilter(
                favoritesOnly, false, false, true, true));
            Assert.False(NotificationAudience.MatchesToastFilter(
                favoritesOnly, true, false, false, false));
        }

        [Fact]
        public void Toast_filter_empty_allows_nothing()
        {
            Assert.False(NotificationAudience.MatchesToastFilter(
                new HashSet<string>(), true, true, true, false));
        }

        [Fact]
        public void ChatItem_personal_counts_as_direct_for_toast()
        {
            var chat = new ChatItem { JID = "me@s.whatsapp.net" };
            chat.Kind = ChatKind.Personal;
            Assert.True(NotificationAudience.MatchesToastFilter(AllTokens(), chat));
        }
    }
}
