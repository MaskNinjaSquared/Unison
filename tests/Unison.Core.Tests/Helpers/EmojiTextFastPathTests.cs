using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    /// <summary>
    /// Guards <see cref="EmojiText.MayContainEmoji"/>, the allocation-free pre-test that lets
    /// virtualised rows skip the grapheme walk. A false negative would silently drop the emoji
    /// font from a name, so the catalog invariant it depends on is asserted here rather than
    /// assumed.
    /// </summary>
    public sealed class EmojiTextFastPathTests
    {
        [Fact]
        public void Every_catalog_glyph_carries_a_char_the_pre_test_detects()
        {
            var offenders = new List<string>();

            foreach (EmojiCategory category in EmojiCatalog.BuiltInCategories)
            {
                IReadOnlyList<string> glyphs = category?.Glyphs;
                if (glyphs == null)
                {
                    continue;
                }

                foreach (string glyph in glyphs)
                {
                    if (!string.IsNullOrEmpty(glyph) && !EmojiText.MayContainEmoji(glyph))
                    {
                        offenders.Add(glyph);
                    }
                }
            }

            // EmojiText.IsEmojiElement also matches on EmojiCatalog.IsKnownGlyph, so a catalog
            // glyph built only from chars below U+200D would be missed by the fast path.
            Assert.Empty(offenders);
        }

        [Theory]
        [InlineData("Lindsay")]
        [InlineData("Mum")]
        [InlineData("Work Group 2024")]
        [InlineData("+1 (615) 807-0571")]
        [InlineData("Jose\u0301 A\u0301lvarez")]
        [InlineData("Jos\u00e9 \u00c1lvarez")]
        [InlineData("\u041c\u0430\u0440\u0438\u044f")]
        public void Plain_names_take_the_fast_path(string text)
        {
            Assert.False(EmojiText.MayContainEmoji(text));
        }

        /// <summary>
        /// The pre-test is deliberately conservative: it only rules emoji *out*. Scripts that
        /// sit above U+200D — CJK, Hangul, and the rest of the BMP tail — still pay for the full
        /// walk. They render identically, just without the saving, which is the safe trade.
        /// </summary>
        [Theory]
        [InlineData("\u6771\u4eac")]
        [InlineData("\ud55c\uad6d")]
        public void Scripts_above_the_threshold_fall_through_but_still_render_plain(string text)
        {
            Assert.True(EmojiText.MayContainEmoji(text));

            IReadOnlyList<EmojiText.Segment> segments = EmojiText.Split(text);
            EmojiText.Segment only = Assert.Single(segments);
            Assert.False(only.IsEmoji);
            Assert.Equal(text, only.Text);
        }

        [Theory]
        [InlineData("\U0001FAE5")]
        [InlineData("look at this \U0001F60A")]
        [InlineData("1\uFE0F\u20E3")]
        [InlineData("\U0001F1EC\U0001F1E7")]
        [InlineData("\U0001F469\u200D\U0001F469\u200D\U0001F467")]
        [InlineData("\U0001F44D\U0001F3FD")]
        [InlineData("\u2764\uFE0F")]
        [InlineData("\u2600")]
        public void Emoji_and_emoji_adjacent_sequences_fall_through(string text)
        {
            Assert.True(EmojiText.MayContainEmoji(text));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Empty_input_needs_no_split(string text)
        {
            Assert.False(EmojiText.MayContainEmoji(text));
        }

        [Theory]
        [InlineData("Lindsay")]
        [InlineData("Work Group 2024")]
        [InlineData("Jos\u00e9 \u00c1lvarez")]
        public void Fast_path_matches_what_Split_would_have_produced(string text)
        {
            Assert.False(EmojiText.MayContainEmoji(text));

            // The skipped call must have yielded exactly one plain segment covering the
            // whole string, which is what painting block.Text directly reproduces.
            IReadOnlyList<EmojiText.Segment> segments = EmojiText.Split(text);
            EmojiText.Segment only = Assert.Single(segments);
            Assert.False(only.IsEmoji);
            Assert.Equal(text, only.Text);
        }
    }
}
