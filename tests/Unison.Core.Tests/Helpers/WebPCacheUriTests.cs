// =============================================================================
// Tests for WebPCacheUri.
// =============================================================================
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class WebPCacheUriTests
    {
        [Fact]
        public void Null_or_blank_is_not_webp()
        {
            Assert.False(WebPCacheUri.Matches(null));
            Assert.False(WebPCacheUri.Matches(""));
            Assert.False(WebPCacheUri.Matches("  "));
        }

        [Theory]
        [InlineData("ms-appdata:///local/x.webp")]
        [InlineData("C:\\cache\\sticker.WEBP")]
        [InlineData("file.webp")]
        public void Webp_uris_match(string uri)
        {
            Assert.True(WebPCacheUri.Matches(uri));
        }

        [Theory]
        [InlineData("ms-appdata:///local/x.png")]
        [InlineData("file.jpg")]
        [InlineData("webp")]
        public void Non_webp_uris_do_not_match(string uri)
        {
            Assert.False(WebPCacheUri.Matches(uri));
        }
    }
}
