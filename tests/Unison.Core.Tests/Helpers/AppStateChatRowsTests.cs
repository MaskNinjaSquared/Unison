using System.Collections.Generic;
using Unison.Core.Models;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class AppStateChatRowsTests
    {
        [Fact]
        public void Existing_rows_are_returned_without_creating()
        {
            var chats = new List<ChatItem>();
            var existing = new List<ChatItem> { new ChatItem { JID = "a@s.whatsapp.net" } };

            List<ChatItem> rows = AppStateChatRows.GetOrAdd(
                chats,
                existing,
                "a@s.whatsapp.net",
                jid => "Name",
                jid => ChatKind.Direct,
                out ChatItem created);

            Assert.Null(created);
            Assert.Equal(1, rows.Count);
            Assert.Empty(chats);
        }

        [Fact]
        public void Empty_list_adds_a_placeholder_row()
        {
            var chats = new List<ChatItem>();

            List<ChatItem> rows = AppStateChatRows.GetOrAdd(
                chats,
                new List<ChatItem>(),
                "b@s.whatsapp.net",
                jid => "Bob",
                jid => ChatKind.Direct,
                out ChatItem created);

            Assert.NotNull(created);
            Assert.Equal("b@s.whatsapp.net", created.JID);
            Assert.Equal("Bob", created.Name);
            Assert.Equal(ChatKind.Direct, created.Kind);
            Assert.Equal(1, chats.Count);
            Assert.Same(created, chats[0]);
            Assert.Same(created, rows[0]);
        }
    }
}
