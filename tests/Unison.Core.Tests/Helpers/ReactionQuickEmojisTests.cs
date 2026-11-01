using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ReactionQuickEmojisTests
    {
        [Fact]
        public void Strip_has_exactly_twelve_non_blank_glyphs()
        {
            Assert.Equal(ReactionQuickEmojis.Count, ReactionQuickEmojis.All.Count);
            Assert.Equal(12, ReactionQuickEmojis.All.Count);
            foreach (string glyph in ReactionQuickEmojis.All)
            {
                Assert.False(string.IsNullOrWhiteSpace(glyph));
            }
        }
    }
}
