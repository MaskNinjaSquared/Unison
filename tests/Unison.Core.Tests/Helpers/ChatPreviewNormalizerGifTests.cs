using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ChatPreviewNormalizerGifTests
    {
        [Fact]
        public void NormalizeBody_strips_gif_tag_without_caption()
        {
            ChatPreviewNormalizer.NormalizeBody(
                "[GIF]",
                ChatPreviewKind.Gif,
                out ChatPreviewKind kind,
                out string text);

            Assert.Equal(ChatPreviewKind.Gif, kind);
            Assert.Equal(string.Empty, text);
        }

        [Fact]
        public void NormalizeBody_keeps_caption_after_gif_tag()
        {
            ChatPreviewNormalizer.NormalizeBody(
                "[GIF] funny cat",
                ChatPreviewKind.Gif,
                out _,
                out string text);

            Assert.Equal("funny cat", text);
        }

        [Fact]
        public void Normalize_preview_strips_gif_tag()
        {
            ChatPreviewNormalizer.Normalize(
                "[GIF]",
                ChatPreviewKind.Gif,
                out _,
                out string text);

            Assert.Equal(string.Empty, text);
        }
    }
}
