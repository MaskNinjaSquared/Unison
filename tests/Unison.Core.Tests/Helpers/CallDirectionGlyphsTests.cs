using Unison.Core.Helpers.Calls;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class CallDirectionGlyphsTests
    {
        [Theory]
        [InlineData(CallDirection.Outgoing, CallDirectionGlyphs.Outgoing)]
        [InlineData(CallDirection.Incoming, CallDirectionGlyphs.Incoming)]
        [InlineData(CallDirection.Missed, CallDirectionGlyphs.Missed)]
        public void Voice_uses_direction_glyphs(CallDirection direction, string expected)
        {
            Assert.Equal(expected, CallDirectionGlyphs.For(direction));
            Assert.Equal(expected, CallDirectionGlyphs.For(direction, isVideo: false));
        }

        [Theory]
        [InlineData(CallDirection.Outgoing)]
        [InlineData(CallDirection.Incoming)]
        [InlineData(CallDirection.Missed)]
        public void Video_uses_video_glyph_for_every_direction(CallDirection direction)
        {
            Assert.Equal(CallDirectionGlyphs.Video, CallDirectionGlyphs.For(direction, isVideo: true));
        }
    }
}
