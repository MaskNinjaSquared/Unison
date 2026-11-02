using System.Collections.Generic;
using Unison.Core.Helpers.Notifications;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class NotificationFilterCodecTests
    {
        [Fact]
        public void Default_serializes_all_three_tokens()
        {
            Assert.Equal("groups;direct;favorites", NotificationFilterCodec.DefaultSerialized);
            Assert.Equal(
                NotificationFilterCodec.DefaultSerialized,
                NotificationFilterCodec.Format(true, true, true));
        }

        [Fact]
        public void Parse_round_trips_and_ignores_unknown_tokens()
        {
            HashSet<string> set = NotificationFilterCodec.Parse("groups;bogus;direct");
            Assert.True(NotificationFilterCodec.Contains(set, NotificationFilterCodec.TokenGroups));
            Assert.True(NotificationFilterCodec.Contains(set, NotificationFilterCodec.TokenDirect));
            Assert.False(NotificationFilterCodec.Contains(set, NotificationFilterCodec.TokenFavorites));
            Assert.Equal("groups;direct", NotificationFilterCodec.Format(set));
        }

        [Fact]
        public void Empty_or_whitespace_parses_to_no_tokens()
        {
            Assert.Empty(NotificationFilterCodec.Parse(null));
            Assert.Empty(NotificationFilterCodec.Parse(""));
            Assert.Empty(NotificationFilterCodec.Parse("   "));
            Assert.Equal(string.Empty, NotificationFilterCodec.Format(false, false, false));
        }

        [Fact]
        public void Scope_formats_and_round_trips_exact_sets()
        {
            Assert.Equal(
                NotificationFilterCodec.DefaultSerialized,
                NotificationFilterCodec.Format(LockScreenNotificationScope.All));
            Assert.Equal("groups", NotificationFilterCodec.Format(LockScreenNotificationScope.Groups));
            Assert.Equal("direct", NotificationFilterCodec.Format(LockScreenNotificationScope.Direct));
            Assert.Equal("favorites", NotificationFilterCodec.Format(LockScreenNotificationScope.Favorites));

            Assert.Equal(
                LockScreenNotificationScope.All,
                NotificationFilterCodec.ToScope(
                    NotificationFilterCodec.Parse(NotificationFilterCodec.DefaultSerialized)));
            Assert.Equal(
                LockScreenNotificationScope.Groups,
                NotificationFilterCodec.ToScope(NotificationFilterCodec.Parse("groups")));
            Assert.Equal(
                LockScreenNotificationScope.Direct,
                NotificationFilterCodec.ToScope(NotificationFilterCodec.Parse("direct")));
            Assert.Equal(
                LockScreenNotificationScope.Favorites,
                NotificationFilterCodec.ToScope(NotificationFilterCodec.Parse("favorites")));
        }

        [Fact]
        public void ToScope_maps_mixed_or_empty_to_All()
        {
            Assert.Equal(
                LockScreenNotificationScope.All,
                NotificationFilterCodec.ToScope(NotificationFilterCodec.Parse("groups;direct")));
            Assert.Equal(
                LockScreenNotificationScope.All,
                NotificationFilterCodec.ToScope(NotificationFilterCodec.Parse(string.Empty)));
        }
    }
}
