using System;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    /// <summary>
    /// Puts one stored message onto the chat-list strip. Was the client's
    /// ApplyMessageAsChatTip: ChatListStrip plus LiveChatPreviewApplier.
    /// </summary>
    public sealed class ChatMessageTipApplyTests
    {
        private static readonly DateTime At = new DateTime(2026, 3, 1, 15, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void A_missing_message_changes_nothing()
        {
            var chat = new ChatItem { JID = "5511988887777@s.whatsapp.net", Name = "Ana" };

            Assert.False(ChatMessageTipApply.Apply(chat, null, false, force: true, "Você", null, "Ontem"));
            Assert.True(string.IsNullOrEmpty(chat.LastMessage));
        }

        [Fact]
        public void A_newer_direct_message_fills_the_strip()
        {
            var chat = new ChatItem { JID = "5511988887777@s.whatsapp.net", Name = "Ana" };
            var message = new ChatMessage
            {
                Id = "m1",
                Content = "oi",
                Timestamp = At,
                IsFromMe = false,
                Status = ChatMessage.StatusDelivered
            };

            Assert.True(ChatMessageTipApply.Apply(
                chat, message, isGroup: false, force: false, "Você", null, "Ontem"));

            Assert.Equal("oi", chat.LastMessage);
            Assert.Equal("m1", chat.LastMessageId);
            Assert.False(chat.LastMessageIsFromMe);
            Assert.Equal(At, chat.LastMessageTimestampUtc);
        }

        [Fact]
        public void A_stale_tip_is_ignored_unless_forced()
        {
            var chat = new ChatItem
            {
                JID = "g@g.us",
                Name = "Família",
                IsGroup = true,
                LastMessage = "novo",
                LastMessageId = "m2",
                LastMessageTimestampUtc = At
            };
            var older = new ChatMessage
            {
                Id = "m1",
                Content = "velho",
                Timestamp = At.AddMinutes(-5),
                IsFromMe = false,
                SenderName = "Ana",
                ParticipantJid = "111@s.whatsapp.net"
            };

            Assert.False(ChatMessageTipApply.Apply(
                chat, older, isGroup: true, force: false, "Você", older.ParticipantJid, "Ontem"));
            Assert.Equal("novo", chat.LastMessage);

            Assert.True(ChatMessageTipApply.Apply(
                chat, older, isGroup: true, force: true, "Você", older.ParticipantJid, "Ontem"));
            Assert.Equal("velho", chat.LastMessage);
            Assert.Equal("Ana", chat.LastMessageSenderName);
        }
    }
}
