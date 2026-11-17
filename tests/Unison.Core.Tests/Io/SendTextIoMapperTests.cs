using System;
using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.WhatsAppClient.Io.Messages;
using Xunit;

namespace Unison.Core.Tests.Io
{
    public sealed class SendTextIoMapperTests
    {
        [Fact]
        public void FromCore_copies_mentions_quote_and_forward_fields()
        {
            var mentions = new OutgoingTextMentions
            {
                Text = "hi @1",
                Mentions = new List<string> { "1@s.whatsapp.net" },
                MentionAll = true,
                MarkAsForwarded = true,
                ForwardingScore = 2,
                Quote = new OutgoingQuote
                {
                    MessageId = "Q1",
                    ChatJid = "g@g.us",
                    ParticipantJid = "2@s.whatsapp.net",
                    AuthorName = "Ana",
                    FromMe = false,
                    Kind = ChatPreviewKind.Image,
                    Text = "pic"
                }
            };

            SendTextRequest request = SendTextRequestMapper.FromCore("g@g.us", mentions);

            Assert.Equal("g@g.us", request.ChatJid);
            Assert.Equal("hi @1", request.Text);
            Assert.Equal(new[] { "1@s.whatsapp.net" }, request.Mentions);
            Assert.True(request.MentionAll);
            Assert.True(request.MarkAsForwarded);
            Assert.Equal(2u, request.ForwardingScore);
            Assert.Equal("Q1", request.Quote.MessageId);
            Assert.Equal("Image", request.Quote.Kind);
            Assert.Equal("Ana", request.Quote.AuthorName);
        }

        [Fact]
        public void ToCoreMentions_round_trips_quote_kind_name()
        {
            var request = new SendTextRequest
            {
                Text = "reply",
                Quote = new OutgoingQuoteRequest
                {
                    MessageId = "Q2",
                    Kind = "Voice",
                    Text = "audio"
                }
            };

            OutgoingTextMentions core = SendTextRequestMapper.ToCoreMentions(request);

            Assert.Equal("reply", core.Text);
            Assert.Equal(ChatPreviewKind.Voice, core.Quote.Kind);
            Assert.Equal("Q2", core.Quote.MessageId);
        }

        [Fact]
        public void ChatMessageDto_round_trips_text_bubble_fields()
        {
            var bubble = new ChatMessage
            {
                Id = "3EB0",
                Content = "hello",
                Timestamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                IsFromMe = true,
                RemoteJid = "555@s.whatsapp.net",
                ParticipantJid = "111@s.whatsapp.net",
                Status = ChatMessage.StatusPending,
                Kind = ChatMessageKind.Text,
                IsForwarded = true,
                MentionAll = true,
                MentionedJids = new List<string> { "2@s.whatsapp.net" },
                QuotedMessageId = "Q",
                QuotedParticipantJid = "3@s.whatsapp.net",
                QuotedText = "earlier",
                QuotedSenderName = "Bob",
                QuotedKind = ChatPreviewKind.Document
            };

            ChatMessageDto dto = ChatMessageDtoMapper.FromBubble(bubble);
            ChatMessage back = ChatMessageDtoMapper.ToBubble(dto);

            Assert.Equal(bubble.Id, back.Id);
            Assert.Equal(bubble.Content, back.Content);
            Assert.Equal(bubble.Timestamp, back.Timestamp);
            Assert.Equal(bubble.Status, back.Status);
            Assert.Equal(bubble.RemoteJid, back.RemoteJid);
            Assert.Equal(bubble.ParticipantJid, back.ParticipantJid);
            Assert.Equal(bubble.Kind, back.Kind);
            Assert.True(back.IsForwarded);
            Assert.True(back.MentionAll);
            Assert.Equal(bubble.MentionedJids, back.MentionedJids);
            Assert.Equal(bubble.QuotedMessageId, back.QuotedMessageId);
            Assert.Equal(ChatPreviewKind.Document, back.QuotedKind);
        }

        [Fact]
        public void ChatMessageDto_round_trips_image_batch_fields()
        {
            var bubble = new ChatMessage
            {
                Id = "IMG1",
                Kind = ChatMessageKind.Image,
                IsImage = true,
                Caption = "look",
                ImageUri = "cache://Image/IMG1_out.jpg",
                LocalMediaBatchId = "batch",
                LocalMediaBatchIndex = 1,
                LocalMediaBatchCount = 3,
                Status = ChatMessage.StatusSent,
                Timestamp = DateTime.UtcNow,
                IsFromMe = true,
                RemoteJid = "555@s.whatsapp.net"
            };

            ChatMessage back = ChatMessageDtoMapper.ToBubble(ChatMessageDtoMapper.FromBubble(bubble));

            Assert.Equal(ChatMessageKind.Image, back.Kind);
            Assert.True(back.IsImage);
            Assert.Equal("look", back.Caption);
            Assert.Equal(bubble.ImageUri, back.ImageUri);
            Assert.Equal("batch", back.LocalMediaBatchId);
            Assert.Equal(1, back.LocalMediaBatchIndex);
            Assert.Equal(3, back.LocalMediaBatchCount);
        }
    }
}
