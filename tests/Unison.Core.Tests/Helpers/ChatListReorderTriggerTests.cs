// =============================================================================
// Tests for ChatListReorderTrigger.
// =============================================================================
using Unison.Core.Models;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ChatListReorderTriggerTests
    {
        [Theory]
        [InlineData(nameof(ChatItem.IsChatPinned))]
        [InlineData(nameof(ChatItem.PinnedTimestamp))]
        [InlineData(nameof(ChatItem.Status))]
        [InlineData(nameof(ChatItem.LinkedParentJid))]
        [InlineData(nameof(ChatItem.IsCommunityParent))]
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

        [Theory]
        [InlineData(nameof(ChatItem.LinkedParentJid))]
        [InlineData(nameof(ChatItem.IsCommunityParent))]
        public void Community_membership_properties_require_projection(string propertyName)
        {
            Assert.True(ChatListReorderTrigger.RequiresCommunityProjection(propertyName));
        }

        [Theory]
        [InlineData(nameof(ChatItem.IsChatPinned))]
        [InlineData(nameof(ChatItem.PinnedTimestamp))]
        [InlineData(nameof(ChatItem.Status))]
        [InlineData(nameof(ChatItem.LastMessage))]
        public void Pin_and_tip_reorder_does_not_require_community_projection(string propertyName)
        {
            Assert.False(ChatListReorderTrigger.RequiresCommunityProjection(propertyName));
        }
    }
}
