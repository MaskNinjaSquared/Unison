using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class EmojiCatalogTests
    {
        [Fact]
        public void BuiltInCategories_are_non_empty_with_glyphs()
        {
            Assert.NotEmpty(EmojiCatalog.BuiltInCategories);

            foreach (EmojiCategory category in EmojiCatalog.BuiltInCategories)
            {
                Assert.False(string.IsNullOrWhiteSpace(category.Id));
                Assert.False(string.IsNullOrWhiteSpace(category.Title));
                Assert.False(string.IsNullOrWhiteSpace(category.JumpGlyph));
                Assert.NotEmpty(category.Glyphs);
                Assert.True(category.JumpFontSize > 0);
            }
        }
    }
}
