using System;
using System.Collections.Generic;
using Unison.Core.Models;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State
{
    public sealed class AliasLinkedMessageFindTests
    {
        [Fact]
        public void The_message_is_found_in_the_other_address()
        {
            var message = new ChatMessage { Id = "m1" };
            var messages = new ChatMessageCache();
            messages.Set("111@lid", new List<ChatMessage> { message });

            string chatJid;
            ChatMessage found;
            bool hit = AliasLinkedMessageFind.TryFind(
                messages,
                new[] { "111@s.whatsapp.net", "111@lid" },
                "m1",
                out chatJid,
                out found);

            Assert.True(hit);
            Assert.Equal("111@lid", chatJid);
            Assert.Same(message, found);
        }

        [Fact]
        public void A_missing_id_or_an_empty_chat_is_not_a_hit()
        {
            var messages = new ChatMessageCache();
            messages.Set("111@s.whatsapp.net", new List<ChatMessage>());

            string chatJid;
            ChatMessage found;
            Assert.False(AliasLinkedMessageFind.TryFind(messages, new[] { "111@s.whatsapp.net" }, "m1", out chatJid, out found));
            Assert.False(AliasLinkedMessageFind.TryFind(messages, new[] { "111@s.whatsapp.net" }, " ", out chatJid, out found));
        }
    }
}
