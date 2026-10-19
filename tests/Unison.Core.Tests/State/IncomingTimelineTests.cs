using System;
using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State
{
    public sealed class IncomingTimelineTests
    {
        private const string Pn = "111@s.whatsapp.net";
        private const string Lid = "111@lid";
        private const string Group = "999@g.us";

        private readonly ChatMessageCache _messages = new ChatMessageCache();
        private readonly MessageIdIndex _index = new MessageIdIndex();
        private readonly List<string> _trimmed = new List<string>();

        private IncomingTimeline Timeline(params string[] aliasChats) =>
            new IncomingTimeline(
                _messages,
                _index,
                jid => aliasChats.Length == 0 ? new[] { jid } : aliasChats,
                JidHelper.Normalize,
                jid => _trimmed.Add(jid));

        [Fact]
        public void A_new_message_is_inserted_in_time_order_registered_and_trimmed()
        {
            _messages.Set(Pn, new List<ChatMessage> { Message("late", 5) });

            IncomingTimelineAcceptResult result = Timeline().Accept(Pn, Message("early", 1), isGroup: false);

            Assert.Equal(IncomingTimelineAcceptKind.Inserted, result.Kind);
            Assert.Equal("early", At(Pn)[0].Id);
            Assert.Equal(new[] { Pn }, _trimmed);
            Assert.True(_index.Contains(Pn, "early"));
            Assert.True(_index.Contains(Pn, "late"));
        }

        [Fact]
        public void A_message_already_in_the_chat_only_upgrades_the_existing_bubble()
        {
            var existing = Message("m1", 1);
            _messages.Set(Pn, new List<ChatMessage> { existing });
            var again = Message("m1", 1);
            again.SenderName = "Ana";

            IncomingTimelineAcceptResult result = Timeline().Accept(Pn, again, isGroup: false);

            Assert.Equal(IncomingTimelineAcceptKind.DuplicateSameChat, result.Kind);
            Assert.Same(existing, result.Message);
            Assert.True(result.ExistingChanged);
            Assert.Equal("Ana", existing.SenderName);
            Assert.Single(At(Pn));
            Assert.Empty(_trimmed);
        }

        [Fact]
        public void A_direct_message_shown_under_the_other_address_moves_here()
        {
            var shown = Message("m1", 1);
            _messages.Set(Lid, new List<ChatMessage> { shown });

            IncomingTimelineAcceptResult result = Timeline(Pn, Lid).Accept(Pn, Message("m1", 1), isGroup: false);

            Assert.Equal(IncomingTimelineAcceptKind.AliasConsolidated, result.Kind);
            Assert.Equal(Lid, result.AliasSourceChatJid);
            Assert.Same(shown, result.Message);
            Assert.Same(shown, Assert.Single(At(Pn)));
            Assert.Empty(At(Lid));
        }

        [Fact]
        public void A_group_never_looks_under_an_alias()
        {
            _messages.Set(Lid, new List<ChatMessage> { Message("m1", 1) });

            IncomingTimelineAcceptResult result = Timeline(Group, Lid).Accept(Group, Message("m1", 1), isGroup: true);

            Assert.Equal(IncomingTimelineAcceptKind.Inserted, result.Kind);
            Assert.Single(At(Lid));
            Assert.Single(At(Group));
        }

        [Fact]
        public void A_message_without_an_id_is_always_inserted()
        {
            _messages.Set(Pn, new List<ChatMessage> { Message(null, 1) });

            IncomingTimelineAcceptResult result = Timeline().Accept(Pn, Message(null, 2), isGroup: false);

            Assert.Equal(IncomingTimelineAcceptKind.Inserted, result.Kind);
            Assert.Equal(2, At(Pn).Count);
        }

        [Fact]
        public void A_blank_chat_or_missing_message_is_left_alone()
        {
            IncomingTimelineAcceptResult blank = Timeline().Accept(" ", Message("m1", 1), isGroup: false);
            IncomingTimelineAcceptResult none = Timeline().Accept(Pn, null, isGroup: false);

            Assert.Equal(IncomingTimelineAcceptKind.Inserted, blank.Kind);
            Assert.Null(none.Message);
            Assert.Empty(_messages.ChatJids());
        }

        [Fact]
        public void The_chat_key_is_normalized_before_it_is_used()
        {
            Timeline().Accept("111:3@s.whatsapp.net", Message("m1", 1), isGroup: false);

            Assert.True(_messages.Contains(Pn));
        }

        private List<ChatMessage> At(string chatJid)
        {
            Assert.True(_messages.TryGet(chatJid, out var list));
            return list;
        }

        private static ChatMessage Message(string id, int minute) =>
            new ChatMessage
            {
                Id = id,
                Timestamp = new DateTime(2026, 1, 1, 0, minute, 0, DateTimeKind.Utc)
            };
    }
}
