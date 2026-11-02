using Unison.Core.Helpers.Chats;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ChatListIncrementalMoveTests
    {
        [Fact]
        public void Absorb_when_linked_child_is_not_on_visible()
        {
            var child = new ChatItem
            {
                JID = "120363111@g.us",
                LinkedParentJid = "120363999@g.us"
            };

            Assert.True(ChatListIncrementalMove.ShouldAbsorbHiddenCatalogueMove(child, hasVisibleRow: false));
        }

        [Fact]
        public void Do_not_absorb_ordinary_chat_missing_from_visible()
        {
            var chat = new ChatItem { JID = "5511999999999@s.whatsapp.net" };

            Assert.False(ChatListIncrementalMove.ShouldAbsorbHiddenCatalogueMove(chat, hasVisibleRow: false));
        }

        [Fact]
        public void Do_not_absorb_when_row_is_already_visible()
        {
            var child = new ChatItem
            {
                JID = "120363111@g.us",
                LinkedParentJid = "120363999@g.us"
            };

            Assert.False(ChatListIncrementalMove.ShouldAbsorbHiddenCatalogueMove(child, hasVisibleRow: true));
        }

        [Fact]
        public void Hub_on_visible_does_not_require_projection_rebuild()
        {
            var hub = new ChatItem
            {
                JID = "120363999@g.us",
                IsCommunityParent = true
            };

            Assert.False(ChatListIncrementalMove.RequiresCommunityProjectionRebuild(hub, hub));
        }

        [Fact]
        public void Linked_child_on_visible_requires_projection_rebuild()
        {
            var child = new ChatItem
            {
                JID = "120363111@g.us",
                LinkedParentJid = "120363999@g.us"
            };

            Assert.True(ChatListIncrementalMove.RequiresCommunityProjectionRebuild(child, child));
        }

        [Fact]
        public void Hidden_linked_child_catalogue_move_does_not_require_rebuild_flag()
        {
            var child = new ChatItem
            {
                JID = "120363111@g.us",
                LinkedParentJid = "120363999@g.us"
            };

            Assert.False(ChatListIncrementalMove.RequiresCommunityProjectionRebuild(null, child));
        }
    }
}
