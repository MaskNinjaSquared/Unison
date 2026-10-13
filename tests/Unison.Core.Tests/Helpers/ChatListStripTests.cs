using System;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    /// <summary>
    /// The fields a stored message contributes to the chat-list strip. Four call sites were
    /// assembling them by hand, and a group author or a direct-chat sender name is easy to
    /// get onto the wrong row.
    /// </summary>
    public class ChatListStripTests
    {
        private static readonly DateTime At = new DateTime(2026, 3, 1, 15, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void A_missing_message_builds_nothing()
        {
            Assert.Null(ChatListStrip.FromMessage(null, false, "Você", null));
        }

        [Fact]
        public void A_direct_message_keeps_the_body_and_drops_the_author()
        {
            var message = new ChatMessage
            {
                Id = "m1",
                Content = "oi",
                Timestamp = At,
                IsFromMe = true,
                Status = ChatMessage.StatusRead,
                SenderName = "Ana",
                ParticipantJid = "5511999999999@s.whatsapp.net"
            };

            ChatListStrip strip = ChatListStrip.FromMessage(message, false, "Você", message.ParticipantJid);

            Assert.Equal("oi", strip.Preview);
            Assert.Equal(At, strip.Timestamp);
            Assert.Equal("m1", strip.MessageId);
            Assert.True(strip.IsFromMe);
            Assert.Equal(MessageSendState.Read, strip.SendState);
            Assert.Equal(string.Empty, strip.AuthorPrefix);
            Assert.Null(strip.ParticipantJid);
            Assert.Null(strip.SenderName);
        }

        [Fact]
        public void A_group_message_keeps_the_other_person_on_the_strip()
        {
            var message = new ChatMessage
            {
                Id = "m2",
                Content = "olá",
                Timestamp = At,
                IsFromMe = false,
                SenderName = "Ana",
                ParticipantJid = "111@lid"
            };

            ChatListStrip strip = ChatListStrip.FromMessage(
                message,
                true,
                "Você",
                "5511999999999@s.whatsapp.net");

            Assert.Equal("Ana", strip.SenderName);
            Assert.Equal("5511999999999@s.whatsapp.net", strip.ParticipantJid);
            Assert.False(string.IsNullOrEmpty(strip.AuthorPrefix));
            Assert.Equal(MessageSendState.NotApplicable, strip.SendState);
        }

        [Fact]
        public void My_own_group_message_does_not_carry_a_sender_name()
        {
            var message = new ChatMessage
            {
                Content = "eu",
                Timestamp = At,
                IsFromMe = true,
                SenderName = "Ana",
                ParticipantJid = "5511999999999@s.whatsapp.net"
            };

            ChatListStrip strip = ChatListStrip.FromMessage(message, true, "Você", message.ParticipantJid);

            Assert.Null(strip.SenderName);
        }
    }
}
