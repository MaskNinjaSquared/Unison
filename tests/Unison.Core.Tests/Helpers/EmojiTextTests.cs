using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class EmojiTextTests
    {
        [Fact]
        public void Split_coalesces_plain_and_emoji_runs()
        {
            var segments = EmojiText.Split("hi 😀 there 🔥!");

            Assert.Equal(5, segments.Count);
            Assert.False(segments[0].IsEmoji);
            Assert.Equal("hi ", segments[0].Text);
            Assert.True(segments[1].IsEmoji);
            Assert.Equal("😀", segments[1].Text);
            Assert.False(segments[2].IsEmoji);
            Assert.Equal(" there ", segments[2].Text);
            Assert.True(segments[3].IsEmoji);
            Assert.Equal("🔥", segments[3].Text);
            Assert.False(segments[4].IsEmoji);
            Assert.Equal("!", segments[4].Text);
        }

        [Fact]
        public void Split_keeps_adjacent_emojis_together()
        {
            var segments = EmojiText.Split("😀🔥");

            Assert.Single(segments);
            Assert.True(segments[0].IsEmoji);
            Assert.Equal("😀🔥", segments[0].Text);
        }

        [Fact]
        public void IsEmojiElement_flags_catalog_and_ranges()
        {
            Assert.True(EmojiText.IsEmojiElement("😀"));
            Assert.True(EmojiText.IsEmojiElement("🇧🇷"));
            Assert.False(EmojiText.IsEmojiElement("a"));
            Assert.False(EmojiText.IsEmojiElement("123"));
        }
    }
}
