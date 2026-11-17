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
    }
}
