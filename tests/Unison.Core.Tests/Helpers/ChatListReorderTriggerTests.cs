// =============================================================================
// Tests for ChatListReorderTrigger.
// =============================================================================
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ChatListReorderTriggerTests
    {
        [Theory]
        [InlineData(nameof(ChatItem.IsChatPinned))]
        [InlineData(nameof(ChatItem.PinnedTimestamp))]
        [InlineData(nameof(ChatItem.Status))]
        public void Order_bearing_properties_require_a_reorder(string propertyName)
        {
            Assert.True(ChatListReorderTrigger.RequiresVisibleReorder(propertyName));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(nameof(ChatItem.MutedUntil))]
        [InlineData(nameof(ChatItem.LastMessage))]
        [InlineData(nameof(ChatItem.Name))]
        [InlineData(nameof(ChatItem.UnreadCount))]
        public void Everything_else_repaints_without_a_reorder(string propertyName)
        {
            Assert.False(ChatListReorderTrigger.RequiresVisibleReorder(propertyName));
        }
    }
}
