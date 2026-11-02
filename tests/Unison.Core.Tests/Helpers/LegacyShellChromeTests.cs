using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class LegacyShellChromeTests
    {
        [Theory]
        [InlineData("chats", true)]
        [InlineData("Archived", true)]
        [InlineData("status", false)]
        [InlineData("calls", false)]
        [InlineData("favorites", false)]
        [InlineData(null, false)]
        public void Only_chat_lists_take_the_pivot_search(string pivot, bool applies)
        {
            Assert.Equal(applies, LegacyPivotSearch.AppliesToChatList(pivot));
        }

        [Theory]
        [InlineData("favorites", true)]
        [InlineData("Favorites", true)]
        [InlineData("chats", false)]
        [InlineData(null, false)]
        public void Favorites_pivot_takes_favourites_search(string pivot, bool applies)
        {
            Assert.Equal(applies, LegacyPivotSearch.AppliesToFavorites(pivot));
        }

        [Theory]
        [InlineData("chats", true)]
        [InlineData("favorites", true)]
        [InlineData("status", false)]
        public void Searchable_pivots_include_chats_and_favorites(string pivot, bool applies)
        {
            Assert.Equal(applies, LegacyPivotSearch.AppliesToSearchablePivot(pivot));
        }

        [Fact]
        public void Back_closes_the_open_settings_category_before_leaving()
        {
            var drill = new SettingsDrillIn();

            Assert.False(drill.TryClose());

            drill.Open("profile");
            Assert.False(drill.IsRoot);
            Assert.True(drill.TryClose());
            Assert.True(drill.IsRoot);
            Assert.False(drill.TryClose());
        }
    }
}
