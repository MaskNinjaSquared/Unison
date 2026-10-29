using System.Collections.Generic;
using System.Linq;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class EmojiCatalogFilterTests
    {
        [Fact]
        public void Filter_blank_query_returns_source_unchanged()
        {
            IReadOnlyList<EmojiCategory> source = EmojiCatalog.BuiltInCategories;

            IReadOnlyList<EmojiCategory> filtered = EmojiCatalog.Filter(source, "   ");

            Assert.Same(source, filtered);
        }

        [Fact]
        public void Filter_by_category_title_keeps_full_matching_section()
        {
            IReadOnlyList<EmojiCategory> filtered = EmojiCatalog.Filter(
                EmojiCatalog.BuiltInCategories,
                "food");

            Assert.Single(filtered);
            Assert.Equal(EmojiCatalog.FoodId, filtered[0].Id);
            Assert.Equal(
                EmojiCatalog.BuiltInCategories.First(c => c.Id == EmojiCatalog.FoodId).Glyphs.Count,
                filtered[0].Glyphs.Count);
        }

        [Fact]
        public void Filter_by_glyph_keeps_only_matching_codepoints()
        {
            IReadOnlyList<EmojiCategory> filtered = EmojiCatalog.Filter(
                EmojiCatalog.BuiltInCategories,
                "😀");

            Assert.NotEmpty(filtered);
            Assert.All(filtered, category =>
            {
                Assert.NotEmpty(category.Glyphs);
                Assert.All(category.Glyphs, glyph => Assert.Contains("😀", glyph));
            });
        }
    }
}
