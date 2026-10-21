// =============================================================================
// ChatItem Name / AvatarUrl must not raise PropertyChanged when the value is
// unchanged — repeated name/avatar apply passes otherwise flash the list.
// =============================================================================
using System.Collections.Generic;
using System.ComponentModel;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Models
{
    public class ChatItemPropertyNotifyTests
    {
        [Fact]
        public void Name_setter_skips_notify_when_unchanged()
        {
            var chat = new ChatItem { Name = "Ana" };
            var raised = new List<string>();
            chat.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            chat.Name = "Ana";

            Assert.Empty(raised);
        }

        [Fact]
        public void Name_setter_notifies_name_and_initial_when_changed()
        {
            var chat = new ChatItem { Name = "Ana" };
            var raised = new List<string>();
            chat.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            chat.Name = "Bruno";

            Assert.Equal(new[] { nameof(ChatItem.Name), nameof(ChatItem.Initial) }, raised);
            Assert.Equal("Bruno", chat.Name);
        }

        [Fact]
        public void AvatarUrl_setter_skips_notify_when_unchanged()
        {
            var chat = new ChatItem { AvatarUrl = "ms-appdata:///local/a.jpg" };
            int count = 0;
            chat.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ChatItem.AvatarUrl))
                {
                    count++;
                }
            };

            chat.AvatarUrl = "ms-appdata:///local/a.jpg";

            Assert.Equal(0, count);
        }

        [Fact]
        public void AvatarUrl_setter_notifies_when_changed()
        {
            var chat = new ChatItem { AvatarUrl = "ms-appdata:///local/a.jpg" };
            int count = 0;
            chat.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ChatItem.AvatarUrl))
                {
                    count++;
                }
            };

            chat.AvatarUrl = "ms-appdata:///local/b.jpg";

            Assert.Equal(1, count);
        }

        [Fact]
        public void UnreadCount_setter_skips_notify_when_unchanged()
        {
            var chat = new ChatItem { UnreadCount = 3 };
            int count = 0;
            chat.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ChatItem.UnreadCount) ||
                    e.PropertyName == nameof(ChatItem.HasUnread) ||
                    e.PropertyName == nameof(ChatItem.UnreadText))
                {
                    count++;
                }
            };

            chat.UnreadCount = 3;

            Assert.Equal(0, count);
        }

        [Fact]
        public void Timestamp_setter_skips_notify_when_unchanged()
        {
            var chat = new ChatItem { Timestamp = "12:00" };
            int count = 0;
            chat.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ChatItem.Timestamp))
                {
                    count++;
                }
            };

            chat.Timestamp = "12:00";

            Assert.Equal(0, count);
        }
    }
}
