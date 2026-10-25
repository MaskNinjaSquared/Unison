using System.Collections.Generic;
using Unison.Core.Helpers.Chats;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ChatListScopedIndexTests
    {
        [Fact]
        public void Counts_only_scoped_rows()
        {
            var a = new ChatItem { JID = "a", Name = "a" };
            var b = new ChatItem { JID = "b", Name = "b" };
            var c = new ChatItem { JID = "c", Name = "c" };
            var source = new List<ChatItem> { a, b, c };

            int index = ChatListScopedIndex.IndexOf(
                source,
                c,
                chat => chat.JID != "b");

            Assert.Equal(1, index);
        }

        [Fact]
        public void Missing_chat_returns_minus_one()
        {
            var a = new ChatItem { JID = "a" };
            int index = ChatListScopedIndex.IndexOf(
                new[] { a },
                new ChatItem { JID = "z" },
                _ => true);

            Assert.Equal(-1, index);
        }

        [Fact]
        public void Already_first_is_zero()
        {
            var a = new ChatItem { JID = "a" };
            var b = new ChatItem { JID = "b" };

            Assert.Equal(0, ChatListScopedIndex.IndexOf(new[] { a, b }, a, _ => true));
        }
    }
}
