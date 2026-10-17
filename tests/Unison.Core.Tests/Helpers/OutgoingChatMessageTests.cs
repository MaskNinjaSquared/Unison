using System;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class OutgoingChatMessageTests
    {
        private static readonly DateTime At = new DateTime(2026, 10, 12, 9, 30, 0, DateTimeKind.Utc);

        private static OutgoingMessageStamp Stamp(string status) => new OutgoingMessageStamp
        {
            Id = "3EB0AA",
            ChatJid = "555@s.whatsapp.net",
            ParticipantJid = "111@s.whatsapp.net",
            TimestampUtc = At,
            Status = status
        };

        [Fact]
        public void Text_is_a_from_me_text_bubble_carrying_the_stamp()
        {
            ChatMessage m = OutgoingChatMessage.Text(Stamp(ChatMessage.StatusPending), "hello");

            Assert.Equal("3EB0AA", m.Id);
            Assert.Equal("hello", m.Content);
            Assert.Equal(ChatMessageKind.Text, m.Kind);
            Assert.True(m.IsFromMe);
            Assert.Equal(At, m.Timestamp);
            Assert.Equal("Me", m.SenderName);
            Assert.Equal("555@s.whatsapp.net", m.RemoteJid);
            Assert.Equal("111@s.whatsapp.net", m.ParticipantJid);
            Assert.Equal(ChatMessage.StatusPending, m.Status);
            Assert.False(m.IsImage);
            Assert.False(m.IsAudio);
        }

        [Fact]
        public void Image_previews_with_the_caption_and_points_at_the_local_copy()
        {
            ChatMessage m = OutgoingChatMessage.Image(Stamp(ChatMessage.StatusSent), "look", "ms-appdata:///local/img.jpg");

            Assert.Equal(ChatMessageKind.Image, m.Kind);
            Assert.True(m.IsImage);
            Assert.Equal("ms-appdata:///local/img.jpg", m.ImageUri);
            Assert.Equal("look", m.Caption);
            Assert.Equal(MediaPreviewTag.ForImage("look"), m.Content);
            Assert.True(m.IsFromMe);
            Assert.Equal("Me", m.SenderName);
            Assert.Equal(ChatMessage.StatusSent, m.Status);
        }

        [Fact]
        public void Image_without_caption_stores_an_empty_caption()
        {
            ChatMessage m = OutgoingChatMessage.Image(Stamp(ChatMessage.StatusSent), null, "uri");

            Assert.Equal(string.Empty, m.Caption);
            Assert.Equal(MediaPreviewTag.ForImage(null), m.Content);
        }

        [Theory]
        [InlineData(true, ChatMessageKind.Voice)]
        [InlineData(false, ChatMessageKind.Audio)]
        public void Audio_kind_follows_the_voice_flag(bool isVoice, ChatMessageKind expected)
        {
            ChatMessage m = OutgoingChatMessage.Audio(Stamp(ChatMessage.StatusRead), "uri.ogg", "audio/ogg; codecs=opus", 7, isVoice);

            Assert.Equal(expected, m.Kind);
            Assert.True(m.IsAudio);
            Assert.Equal(isVoice, m.IsVoiceMessage);
            Assert.Equal("uri.ogg", m.AudioUri);
            Assert.Equal("audio/ogg; codecs=opus", m.AudioMimeType);
            Assert.Equal(7u, m.AudioDurationSeconds);
            Assert.Equal(MediaPreviewTag.ForAudio(isVoice), m.Content);
            Assert.Equal(ChatMessage.StatusRead, m.Status);
        }

        [Fact]
        public void Document_previews_with_the_file_name_and_points_at_the_local_copy()
        {
            ChatMessage m = OutgoingChatMessage.Document(
                Stamp(ChatMessage.StatusPending),
                "ms-appdata:///local/doc.pdf",
                "contract.pdf",
                "application/pdf",
                4096,
                null);

            Assert.Equal(ChatMessageKind.Document, m.Kind);
            Assert.True(m.IsDocument);
            Assert.Equal("ms-appdata:///local/doc.pdf", m.DocumentUri);
            Assert.Equal("contract.pdf", m.DocumentFileName);
            Assert.Equal("application/pdf", m.DocumentMimeType);
            Assert.Equal(4096L, m.DocumentFileLengthBytes);
            Assert.Equal(string.Empty, m.Caption);
            Assert.Equal(MediaPreviewTag.ForDocument("contract.pdf"), m.Content);
            Assert.Equal(ChatMessage.StatusPending, m.Status);
        }
    }
}
