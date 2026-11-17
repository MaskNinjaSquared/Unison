using System.Collections.Generic;
using Unison.Core.Helpers.Chats;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class VisibleMoveMissPolicyTests
    {
        [Theory]
        [InlineData(false, 10, true, true, false)]
        [InlineData(true, 0, true, true, false)]
        [InlineData(true, 10, false, true, false)]
        [InlineData(true, 10, true, false, false)]
        [InlineData(true, 10, true, true, true)]
        [InlineData(true, 257, true, true, true)]
        public void PreferInPlaceReorder_only_frugal_painted_unfiltered(
            bool preferFrugal,
            int visibleCount,
            bool filterIsAll,
            bool searchEmpty,
            bool expect)
        {
            Assert.Equal(
                expect,
                VisibleMoveMissPolicy.PreferInPlaceReorder(
                    preferFrugal,
                    visibleCount,
                    filterIsAll,
                    searchEmpty));
        }

        [Fact]
        public void HasLinkedChildrenOnRoot_detects_subgroup_still_painted_flat()
        {
            var visible = new List<ChatItem>
            {
                new ChatItem { JID = "hub@g.us", IsCommunityParent = true },
                new ChatItem { JID = "child@g.us", LinkedParentJid = "hub@g.us" }
            };

            Assert.True(VisibleMoveMissPolicy.HasLinkedChildrenOnRoot(visible));
            Assert.False(VisibleMoveMissPolicy.HasLinkedChildrenOnRoot(
                new List<ChatItem> { visible[0] }));
        }
    }
}
