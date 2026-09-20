// =============================================================================
// Tests for IncomingChatMessageSnapshot.
//
// The incoming pump used to hand-build ChatMessageContentSnapshot (and had a
// fallback that built ChatMessage without media flags). FromRender is the only
// construction path now.
// =============================================================================
using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class IncomingChatMessageSnapshotTests
    {
        [Fact]
        public void Media_flags_and_caption_come_from_the_render_info()
        {
            var render = new MessageRenderInfo
            {
                IsImage = true,
                Caption = "beach"
            };

            ChatMessageContentSnapshot snapshot = IncomingChatMessageSnapshot.FromRender(
                render,
                text: "beach",
                isForwarded: false,
                quotedText: null,
                quotedKind: ChatPreviewKind.Text,
                quotedSenderName: null,
                quotedParticipantJid: null,
                quotedMessageId: null,
                mentionedJids: null);

            Assert.True(snapshot.IsImage);
            Assert.False(snapshot.IsSticker);
            Assert.Equal("beach", snapshot.Caption);
            Assert.Equal("beach", snapshot.Text);
        }

        [Fact]
        public void Quote_and_forward_fields_pass_through()
        {
            var mentions = new List<string> { "5511999999999@s.whatsapp.net" };

            ChatMessageContentSnapshot snapshot = IncomingChatMessageSnapshot.FromRender(
                renderInfo: null,
                text: "reply",
                isForwarded: true,
                quotedText: "original",
                quotedKind: ChatPreviewKind.Image,
                quotedSenderName: "Ana",
                quotedParticipantJid: "5511888888888@s.whatsapp.net",
                quotedMessageId: "3EB0",
                mentionedJids: mentions);

            Assert.True(snapshot.IsForwarded);
            Assert.Equal("original", snapshot.QuotedText);
            Assert.Equal(ChatPreviewKind.Image, snapshot.QuotedKind);
            Assert.Equal("Ana", snapshot.QuotedSenderName);
            Assert.Equal("5511888888888@s.whatsapp.net", snapshot.QuotedParticipantJid);
            Assert.Equal("3EB0", snapshot.QuotedMessageId);
            Assert.Same(mentions, snapshot.MentionedJids);
        }

        [Fact]
        public void A_null_render_still_carries_plain_text()
        {
            ChatMessageContentSnapshot snapshot = IncomingChatMessageSnapshot.FromRender(
                null,
                "hello",
                false,
                null,
                ChatPreviewKind.Text,
                null,
                null,
                null,
                null);

            Assert.Equal("hello", snapshot.Text);
            Assert.Equal(string.Empty, snapshot.Caption);
            Assert.False(snapshot.IsImage);
        }

        [Fact]
        public void A_sticker_is_flagged_without_turning_into_an_image_only()
        {
            var render = new MessageRenderInfo { IsSticker = true, Caption = "" };

            ChatMessageContentSnapshot snapshot = IncomingChatMessageSnapshot.FromRender(
                render,
                text: string.Empty,
                isForwarded: false,
                quotedText: null,
                quotedKind: ChatPreviewKind.Text,
                quotedSenderName: null,
                quotedParticipantJid: null,
                quotedMessageId: null,
                mentionedJids: null);

            Assert.True(snapshot.IsSticker);
            Assert.False(snapshot.IsImage);
        }
    }
}
