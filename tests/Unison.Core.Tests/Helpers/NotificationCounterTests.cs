using System.Collections.ObjectModel;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class NotificationCounterTests
    {
        [Fact]
        public void Only_chats_with_unread_messages_count()
        {
            var chats = new[]
            {
                new ChatItem { UnreadCount = 2 },
                new ChatItem { UnreadCount = 0 },
                new ChatItem { UnreadCount = 1 },
                null
            };

            Assert.Equal(2, NotificationCounter.CountChatsWithNotification(chats));
            Assert.Equal("(2)", NotificationCounter.FormatBadge(2));
            Assert.Equal(string.Empty, NotificationCounter.FormatBadge(0));
            Assert.Equal("(99)", NotificationCounter.FormatBadge(99));
            Assert.Equal("(99" + NotificationCounter.SuperscriptPlus + ")", NotificationCounter.FormatBadge(100));
            Assert.Equal("(99" + NotificationCounter.SuperscriptPlus + ")", NotificationCounter.FormatBadge(250));
            Assert.Equal("99", NotificationCounter.FormatOverflowCount(99));
            Assert.Equal("99" + NotificationCounter.SuperscriptPlus, NotificationCounter.FormatOverflowCount(100));
        }

        [Fact]
        public void The_watch_follows_arrivals_departures_and_unread_changes()
        {
            var chats = new ObservableCollection<ChatItem>
            {
                new ChatItem { UnreadCount = 1 }
            };
            var watch = new NotificationCounterWatch(chats);
            int raised = 0;
            watch.Changed += (s, e) => raised++;

            Assert.Equal(1, watch.Count);
            Assert.Equal("(1)", watch.BadgeText);

            var quiet = new ChatItem();
            chats.Add(quiet);
            Assert.Equal(1, watch.Count);
            Assert.Equal(0, raised);

            quiet.UnreadCount = 4;
            Assert.Equal(2, watch.Count);
            Assert.Equal(1, raised);

            chats.RemoveAt(0);
            Assert.Equal(1, watch.Count);

            watch.Dispose();
            quiet.UnreadCount = 0;
            Assert.Equal(1, watch.Count);
        }
    }
}
