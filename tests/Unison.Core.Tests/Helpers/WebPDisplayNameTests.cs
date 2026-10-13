using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class WebPDisplayNameTests
    {
        [Fact]
        public void A_cached_webp_names_the_png_sibling_after_its_own_file()
        {
            Assert.Equal(
                "abc",
                WebPDisplayName.FileBaseOrSkip("ms-appdata:///local/MediaCache/Images/abc.webp"));
        }

        [Fact]
        public void A_display_sibling_and_a_blank_uri_are_left_alone()
        {
            Assert.Null(WebPDisplayName.FileBaseOrSkip("ms-appdata:///local/MediaCache/Images/abc_display.webp"));
            Assert.Null(WebPDisplayName.FileBaseOrSkip("ms-appdata:///local/MediaCache/Images/abc_Display.WEBP"));
            Assert.Null(WebPDisplayName.FileBaseOrSkip(null));
            Assert.Null(WebPDisplayName.FileBaseOrSkip("  "));
        }
    }
}
