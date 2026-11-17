using System;
using Unison.Core.Models;
using Unison.WhatsAppClient.Io.Messages;
using Xunit;

namespace Unison.Core.Tests.Io
{
    public sealed class MessageTargetRequestMapperTests
    {
        [Fact]
        public void ToReact_copies_target_fields_and_emoji()
        {
            var bubble = new ChatMessage
            {
                Id = "M1",
                RemoteJid = "555@s.whatsapp.net",
                ParticipantJid = "111@s.whatsapp.net",
                IsFromMe = true,
                Timestamp = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc)
            };

            ReactToMessageRequest request = MessageTargetRequestMapper.ToReact("g@g.us", bubble, "👍");

            Assert.Equal("g@g.us", request.ChatJid);
            Assert.Equal("M1", request.MessageId);
            Assert.Equal("555@s.whatsapp.net", request.RemoteJid);
            Assert.Equal("111@s.whatsapp.net", request.ParticipantJid);
            Assert.True(request.IsFromMe);
            Assert.Equal(bubble.Timestamp, request.TimestampUtc);
            Assert.Equal("👍", request.Emoji);
        }

        [Fact]
        public void ToPin_copies_target_and_duration()
        {
            var bubble = new ChatMessage { Id = "P1", IsFromMe = false };
            PinMessageRequest request = MessageTargetRequestMapper.ToPin("c@g.us", bubble, true, 86400);

            Assert.Equal("P1", request.MessageId);
            Assert.True(request.Pin);
            Assert.Equal(86400u, request.DurationSeconds);
        }

        [Fact]
        public void ToDeleteForMe_flags_media_and_revoked()
        {
            var bubble = new ChatMessage
            {
                Id = "D1",
                IsImage = true,
                IsRevoked = false
            };

            DeleteMessageForMeRequest request = MessageTargetRequestMapper.ToDeleteForMe("c", bubble);

            Assert.True(request.DeleteMedia);
            Assert.False(request.IsRevoked);
        }

        [Fact]
        public void ToDeleteForEveryone_copies_group_admin_flags()
        {
            var bubble = new ChatMessage { Id = "D2", IsFromMe = false };
            DeleteMessageForEveryoneRequest request = MessageTargetRequestMapper.ToDeleteForEveryone(
                "g@g.us",
                bubble,
                isGroupChat: true,
                viewerIsGroupAdmin: true);

            Assert.True(request.IsGroupChat);
            Assert.True(request.ViewerIsGroupAdmin);
            Assert.False(request.IsFromMe);
        }
    }
}
