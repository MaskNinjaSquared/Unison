// =============================================================================
// Tests for IncomingMediaMetadata.
//
// Proto media sub-messages onto ChatMessage fields the download path reads.
// Lived as five private statics on WhatsAppService; the pump called them one by
// one from MessageRenderInfo flags. One Apply keeps the order (sticker before
// image is elsewhere; here each kind is exclusive on a well-formed envelope).
// =============================================================================
using Google.Protobuf;
using Proto;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class IncomingMediaMetadataTests
    {
        private static ByteString KeyBytes => ByteString.CopyFrom(1, 2, 3, 4);

        [Fact]
        public void An_image_writes_kind_keys_and_caption()
        {
            var target = new ChatMessage();
            var image = new Message.Types.ImageMessage
            {
                Mimetype = "image/jpeg",
                Url = "https://mmg.example/img",
                DirectPath = "/v/t62/img",
                MediaKey = KeyBytes,
                FileEncSha256 = KeyBytes,
                Caption = "beach"
            };

            IncomingMediaMetadata.ApplyImage(target, image);

            Assert.Equal(ChatMessageKind.Image, target.Kind);
            Assert.Equal("image/jpeg", target.ImageMimeType);
            Assert.Equal("https://mmg.example/img", target.ImageUrl);
            Assert.Equal("/v/t62/img", target.ImageDirectPath);
            Assert.False(string.IsNullOrEmpty(target.ImageMediaKeyBase64));
            Assert.False(string.IsNullOrEmpty(target.ImageFileEncSha256Base64));
            Assert.Equal("beach", target.Caption);
        }

        [Fact]
        public void A_voice_note_marks_ptt_and_duration()
        {
            var target = new ChatMessage();
            var audio = new Message.Types.AudioMessage
            {
                Ptt = true,
                Seconds = 12,
                Mimetype = "audio/ogg; codecs=opus",
                Url = "https://mmg.example/aud",
                DirectPath = "/v/t62/aud",
                MediaKey = KeyBytes,
                FileEncSha256 = KeyBytes
            };

            IncomingMediaMetadata.ApplyAudio(target, audio);

            Assert.True(target.IsAudio);
            Assert.True(target.IsVoiceMessage);
            Assert.Equal(12u, target.AudioDurationSeconds);
            Assert.Equal("audio/ogg; codecs=opus", target.AudioMimeType);
        }

        [Fact]
        public void A_document_keeps_file_name_and_length()
        {
            var target = new ChatMessage();
            var document = new Message.Types.DocumentMessage
            {
                FileName = "scan.pdf",
                Mimetype = "application/pdf",
                Url = "https://mmg.example/doc",
                DirectPath = "/v/t62/doc",
                MediaKey = KeyBytes,
                FileEncSha256 = KeyBytes,
                FileLength = 4096
            };

            IncomingMediaMetadata.ApplyDocument(target, document);

            Assert.Equal(ChatMessageKind.Document, target.Kind);
            Assert.Equal("scan.pdf", target.DocumentFileName);
            Assert.Equal(4096L, target.DocumentFileLengthBytes);
        }

        [Fact]
        public void Apply_from_render_info_picks_the_sticker_arm()
        {
            var target = new ChatMessage();
            var info = new MessageRenderInfo
            {
                IsSticker = true,
                StickerMessage = new Message.Types.StickerMessage
                {
                    Mimetype = "image/webp",
                    Url = "https://mmg.example/sticker",
                    DirectPath = "/v/t62/sticker",
                    MediaKey = KeyBytes,
                    FileEncSha256 = KeyBytes
                }
            };

            IncomingMediaMetadata.Apply(target, info);

            Assert.Equal(ChatMessageKind.Sticker, target.Kind);
            Assert.False(target.IsStickerFailed);
            Assert.Equal("image/webp", target.ImageMimeType);
        }

        [Fact]
        public void Apply_does_nothing_when_there_is_no_media()
        {
            var target = new ChatMessage { Kind = ChatMessageKind.Text, Content = "hi" };
            IncomingMediaMetadata.Apply(target, new MessageRenderInfo());
            Assert.Equal(ChatMessageKind.Text, target.Kind);
            Assert.Equal("hi", target.Content);
        }

        [Fact]
        public void A_null_target_or_proto_is_a_no_op()
        {
            IncomingMediaMetadata.ApplyImage(null, new Message.Types.ImageMessage());
            IncomingMediaMetadata.ApplyImage(new ChatMessage(), null);
        }
    }
}
