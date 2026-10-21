// =============================================================================
// Tests for ChatListNamesAppliedRefresh.
// Mass name apply must not rebuild the visible list unless search membership can change.
// =============================================================================
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ChatListNamesAppliedRefreshTests
    {
        [Fact]
        public void Null_search_repaints_via_bindings_without_a_rebuild()
        {
            Assert.False(ChatListNamesAppliedRefresh.RequiresVisibleRebuild(null));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void Idle_list_repaints_via_bindings_without_a_rebuild(string searchQuery)
        {
            Assert.False(ChatListNamesAppliedRefresh.RequiresVisibleRebuild(searchQuery));
        }

        [Theory]
        [InlineData("ana")]
        [InlineData("  bob  ")]
        public void Active_search_rebuilds_so_membership_can_follow_new_titles(string searchQuery)
        {
            Assert.True(ChatListNamesAppliedRefresh.RequiresVisibleRebuild(searchQuery));
        }
    }
}
