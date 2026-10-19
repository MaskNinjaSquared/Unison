using System;
using System.Collections.Generic;
using Unison.Core.Models;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State
{
    public sealed class AliasDuplicateMessageMoveTests
    {
        [Fact]
        public void The_bubble_moves_from_the_alias_chat_onto_the_target()
        {
            var source = new List<ChatMessage> { Message("m1", 1) };
            var messages = new ChatMessageCache();
            messages.Set("111@s.whatsapp.net", source);
            var index = new MessageIdIndex();
            index.Register("111@s.whatsapp.net", "m1");

            ChatMessage moved;
            bool movedIt = AliasDuplicateMessageMove.TryMove(
                messages,
                index,
                "222@s.whatsapp.net",
                "111@s.whatsapp.net",
                "m1",
                out moved);

            Assert.True(movedIt);
            Assert.Equal("m1", moved.Id);
            Assert.Empty(source);
            Assert.Equal("m1", At(messages, "222@s.whatsapp.net")[0].Id);
            Assert.True(index.Contains("222@s.whatsapp.net", "m1"));
            Assert.False(index.Contains("111@s.whatsapp.net", "m1"));
        }

        [Fact]
        public void A_target_that_already_has_the_id_does_not_gain_a_second_copy()
        {
            var already = Message("m1", 1);
            var duplicate = Message("m1", 2);
            var messages = new ChatMessageCache();
            messages.Set("222@s.whatsapp.net", new List<ChatMessage> { already });
            messages.Set("111@s.whatsapp.net", new List<ChatMessage> { duplicate });
            var index = new MessageIdIndex();
            index.Register("222@s.whatsapp.net", "m1");
            index.Register("111@s.whatsapp.net", "m1");

            ChatMessage moved;
            Assert.True(AliasDuplicateMessageMove.TryMove(
                messages, index, "222@s.whatsapp.net", "111@s.whatsapp.net", "m1", out moved));

            Assert.Same(duplicate, moved);
            Assert.Single(At(messages, "222@s.whatsapp.net"));
            Assert.Same(already, At(messages, "222@s.whatsapp.net")[0]);
            Assert.Empty(At(messages, "111@s.whatsapp.net"));
        }

        [Fact]
        public void The_same_chat_a_blank_id_or_a_missing_source_does_not_move()
        {
            var messages = new ChatMessageCache();
            messages.Set("111@s.whatsapp.net", new List<ChatMessage> { Message("m1", 1) });
            var index = new MessageIdIndex();
            ChatMessage moved;

            Assert.False(AliasDuplicateMessageMove.TryMove(
                messages, index, "111@s.whatsapp.net", "111@S.WHATSAPP.NET", "m1", out moved));
            Assert.False(AliasDuplicateMessageMove.TryMove(
                messages, index, "222@s.whatsapp.net", "111@s.whatsapp.net", " ", out moved));
            Assert.False(AliasDuplicateMessageMove.TryMove(
                messages, index, "222@s.whatsapp.net", "333@s.whatsapp.net", "m1", out moved));
            Assert.Single(At(messages, "111@s.whatsapp.net"));
        }

        private static List<ChatMessage> At(ChatMessageCache messages, string chatJid)
        {
            Assert.True(messages.TryGet(chatJid, out var list));
            return list;
        }

        private static ChatMessage Message(string id, int minute)
        {
            return new ChatMessage
            {
                Id = id,
                Timestamp = new DateTime(2026, 1, 1, 0, minute, 0, DateTimeKind.Utc)
            };
        }
    }
}
