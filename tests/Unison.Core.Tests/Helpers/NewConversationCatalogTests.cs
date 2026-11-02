// =============================================================================
// Tests for NewConversationCatalog — New Conversation A–Z candidates.
// =============================================================================
using System.Collections.Generic;
using Unison.Core.Helpers.Chats;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class NewConversationCatalogTests
    {
        [Fact]
        public void Build_null_input_returns_empty()
        {
            IReadOnlyList<ChatItem> result = NewConversationCatalog.Build(null, null, null);
            Assert.Empty(result);
        }

        [Fact]
        public void Build_keeps_direct_only_excludes_self_group_and_personal()
        {
            var chats = new List<ChatItem>
            {
                new ChatItem { JID = "alice@s.whatsapp.net", Name = "Alice", Kind = ChatKind.Direct },
                new ChatItem { JID = "me@s.whatsapp.net", Name = "Me", Kind = ChatKind.Personal },
                new ChatItem { JID = "g@g.us", Name = "Group", Kind = ChatKind.Group },
                new ChatItem { JID = "self-direct@s.whatsapp.net", Name = "Self", Kind = ChatKind.Direct },
                null,
            };

            IReadOnlyList<ChatItem> result = NewConversationCatalog.Build(
                chats,
                jid => jid != null && jid.StartsWith("self-"),
                c => c.Name);

            Assert.Single(result);
            Assert.Equal("Alice", result[0].Name);
        }

        [Fact]
        public void Build_adds_phone_book_stubs_not_already_in_chats()
        {
            var chats = new List<ChatItem>
            {
                new ChatItem { JID = "alice@s.whatsapp.net", Name = "Alice", Kind = ChatKind.Direct },
            };
            var phone = new Dictionary<string, string>
            {
                { "alice@s.whatsapp.net", "Alice Agenda" },
                { "bob@s.whatsapp.net", "Bob" },
            };

            IReadOnlyList<ChatItem> result = NewConversationCatalog.Build(
                chats,
                _ => false,
                c => c.Name,
                phoneContactNamesByJid: phone);

            Assert.Equal(2, result.Count);
            Assert.Equal("Alice", result[0].Name);
            Assert.Equal("Bob", result[1].Name);
        }

        [Fact]
        public void Build_filters_by_search_query()
        {
            var chats = new List<ChatItem>
            {
                new ChatItem { JID = "alice@s.whatsapp.net", Name = "Alice", Kind = ChatKind.Direct },
                new ChatItem { JID = "bob@s.whatsapp.net", Name = "Bob", Kind = ChatKind.Direct },
            };

            IReadOnlyList<ChatItem> result = NewConversationCatalog.Build(
                chats,
                _ => false,
                c => c.Name,
                searchQuery: "bo");

            Assert.Single(result);
            Assert.Equal("Bob", result[0].Name);
        }
    }
}
