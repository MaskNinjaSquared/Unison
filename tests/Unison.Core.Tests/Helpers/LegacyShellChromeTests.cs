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
