using System.Linq;
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

        [Fact]
        public void ForStrip_without_mine_keeps_the_default_twelve()
        {
            Assert.Equal(ReactionQuickEmojis.All, ReactionQuickEmojis.ForStrip(null));
            Assert.Equal(ReactionQuickEmojis.All, ReactionQuickEmojis.ForStrip("   "));
        }

        [Fact]
        public void ForStrip_moves_a_default_mine_to_the_front_without_growing()
        {
            string mine = ReactionQuickEmojis.All[5];

            var strip = ReactionQuickEmojis.ForStrip(mine);

            Assert.Equal(12, strip.Count);
            Assert.Equal(mine, strip[0]);
            Assert.Equal(1, strip.Count(g => g == mine));
            Assert.Equal(
                ReactionQuickEmojis.All.Where(g => g != mine).ToArray(),
                strip.Skip(1).ToArray());
        }

        [Fact]
        public void ForStrip_prepends_a_custom_mine_as_a_thirteenth()
        {
            const string mine = "🦄";

            var strip = ReactionQuickEmojis.ForStrip(mine);

            Assert.Equal(13, strip.Count);
            Assert.Equal(mine, strip[0]);
            Assert.Equal(ReactionQuickEmojis.All, strip.Skip(1).ToArray());
        }
    }
}
