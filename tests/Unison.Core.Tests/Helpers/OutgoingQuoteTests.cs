using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class OutgoingQuoteTests
    {
        [Fact]
        public void From_text_keeps_the_body_and_the_author()
        {
            var source = new ChatMessage
            {
                Id = "ABC",
                RemoteJid = "120363@g.us",
                ParticipantJid = "5511@s.whatsapp.net",
                SenderName = "Ana",
                IsFromMe = false,
                Kind = ChatMessageKind.Text,
                Content = "oi"
            };

            OutgoingQuote quote = OutgoingQuote.From(source, "120363@g.us");

            Assert.Equal("ABC", quote.MessageId);
            Assert.Equal("120363@g.us", quote.ChatJid);
            Assert.Equal("5511@s.whatsapp.net", quote.ParticipantJid);
            Assert.Equal("Ana", quote.AuthorName);
            Assert.False(quote.FromMe);
            Assert.Equal(ChatPreviewKind.Text, quote.Kind);
            Assert.Equal("oi", quote.Text);
        }

        [Fact]
        public void From_image_uses_the_caption()
        {
            var source = new ChatMessage
            {
                Id = "IMG",
                RemoteJid = "5511@s.whatsapp.net",
                Kind = ChatMessageKind.Image,
                Caption = "foto",
                Content = "ignored"
            };

            OutgoingQuote quote = OutgoingQuote.From(source, null);

            Assert.Equal(ChatPreviewKind.Image, quote.Kind);
            Assert.Equal("foto", quote.Text);
            Assert.True(string.IsNullOrEmpty(quote.ParticipantJid));
        }

        [Fact]
        public void From_private_chat_does_not_invent_a_participant()
        {
            var mine = new ChatMessage
            {
                Id = "MINE",
                RemoteJid = "5511@s.whatsapp.net",
                IsFromMe = true,
                Kind = ChatMessageKind.Text,
                Content = "eu"
            };
            var theirs = new ChatMessage
            {
                Id = "THEIRS",
                RemoteJid = "5511@s.whatsapp.net",
                IsFromMe = false,
                Kind = ChatMessageKind.Text,
                Content = "ele"
            };

            OutgoingQuote fromMe = OutgoingQuote.From(mine, "5511@s.whatsapp.net");
            OutgoingQuote incoming = OutgoingQuote.From(theirs, "2233@lid");

            Assert.True(string.IsNullOrEmpty(fromMe.ParticipantJid));
            Assert.True(fromMe.FromMe);
            Assert.True(string.IsNullOrEmpty(incoming.ParticipantJid));
            Assert.False(incoming.FromMe);
            Assert.Equal("2233@lid", incoming.ChatJid);
        }

        [Fact]
        public void From_group_without_an_author_does_not_use_the_group_jid()
        {
            var source = new ChatMessage
            {
                Id = "GRP",
                RemoteJid = "120363@g.us",
                Kind = ChatMessageKind.Text,
                Content = "oi"
            };

            OutgoingQuote quote = OutgoingQuote.From(source, "120363@g.us");

            Assert.True(string.IsNullOrEmpty(quote.ParticipantJid));
            Assert.Equal("120363@g.us", quote.ChatJid);
        }

        [Fact]
        public void From_document_uses_the_file_name_when_there_is_no_caption()
        {
            var source = new ChatMessage
            {
                Id = "DOC",
                Kind = ChatMessageKind.Document,
                DocumentFileName = "nota.pdf"
            };

            OutgoingQuote quote = OutgoingQuote.From(source, "me@s.whatsapp.net");

            Assert.Equal(ChatPreviewKind.Document, quote.Kind);
            Assert.Equal("nota.pdf", quote.Text);
        }

        [Fact]
        public void From_returns_null_without_an_id()
        {
            Assert.Null(OutgoingQuote.From(new ChatMessage { Content = "x" }, "jid"));
            Assert.Null(OutgoingQuote.From(null, "jid"));
        }

        [Fact]
        public void ApplyQuote_fills_the_optimistic_bubble()
        {
            var bubble = new ChatMessage();
            OutgoingChatMessage.ApplyQuote(bubble, new OutgoingQuote
            {
                MessageId = "ABC",
                ParticipantJid = "5511@s.whatsapp.net",
                AuthorName = "Ana",
                Kind = ChatPreviewKind.Video,
                Text = "clipe"
            });

            Assert.Equal("ABC", bubble.QuotedMessageId);
            Assert.Equal("5511@s.whatsapp.net", bubble.QuotedParticipantJid);
            Assert.Equal("Ana", bubble.QuotedSenderName);
            Assert.Equal(ChatPreviewKind.Video, bubble.QuotedKind);
            Assert.Equal("clipe", bubble.QuotedText);
        }
    }
}
