using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Models
{
    public sealed class ChatItemCommunityLinkTests
    {
        private const string Parent = "120363111111111111@g.us";
        private const string Child = "120363222222222222@g.us";

        [Fact]
        public void IsCommunityParent_sets_Kind_Community()
        {
            var chat = new ChatItem { Kind = ChatKind.Group, JID = Parent };

            chat.IsCommunityParent = true;

            Assert.Equal(ChatKind.Community, chat.Kind);
            Assert.True(chat.IsCommunity);
            Assert.False(chat.IsGroup);
        }

        [Fact]
        public void ApplyKind_preserves_Community_when_parent_flag_set()
        {
            var chat = new ChatItem
            {
                JID = Parent,
                IsCommunityParent = true
            };

            chat.ApplyKind(Parent, isSelfChat: false);

            Assert.Equal(ChatKind.Community, chat.Kind);
        }

        [Fact]
        public void HistoryChatPreviewApplier_roundtrips_parent_and_linked_parent()
        {
            var source = new ChatItem
            {
                JID = Child,
                Name = "Subgrupo",
                Kind = ChatKind.Group,
                LinkedParentJid = Parent,
                LastMessage = "hi",
                LastMessageKind = ChatPreviewKind.Text,
                LastMessageTimestampUtc = System.DateTime.UtcNow
            };

            HistoryChatPreview preview = HistoryChatPreviewApplier.FromChatItem(source);
            Assert.NotNull(preview);
            Assert.Equal(Parent, preview.LinkedParentJid);
            Assert.False(preview.IsCommunityParent);

            ChatItem hydrated = HistoryChatPreviewApplier.ToChatItem(preview);
            Assert.NotNull(hydrated);
            Assert.Equal(Parent, hydrated.LinkedParentJid);
            Assert.Equal(ChatKind.Group, hydrated.Kind);
        }

        [Fact]
        public void HistoryChatPreviewApplier_roundtrips_community_parent_kind()
        {
            var source = new ChatItem
            {
                JID = Parent,
                Name = "Comunidade",
                IsCommunityParent = true,
                LastMessage = "hi",
                LastMessageKind = ChatPreviewKind.Text,
                LastMessageTimestampUtc = System.DateTime.UtcNow
            };

            Assert.Equal(ChatKind.Community, source.Kind);

            HistoryChatPreview preview = HistoryChatPreviewApplier.FromChatItem(source);
            Assert.NotNull(preview);
            Assert.True(preview.IsCommunityParent);
            // Catalog still marks @g.us as group for list/strip rules.
            Assert.True(preview.IsGroup);

            ChatItem hydrated = HistoryChatPreviewApplier.ToChatItem(preview);
            Assert.NotNull(hydrated);
            Assert.True(hydrated.IsCommunityParent);
            Assert.Equal(ChatKind.Community, hydrated.Kind);
        }

        [Fact]
        public void ApplyIfNewer_copies_community_link_fields()
        {
            var target = new ChatItem
            {
                JID = Child,
                Name = "Subgrupo",
                Kind = ChatKind.Group,
                LastMessage = "old",
                LastMessageKind = ChatPreviewKind.Text,
                LastMessageTimestampUtc = System.DateTime.UtcNow.AddMinutes(-1)
            };

            var preview = new HistoryChatPreview
            {
                Jid = Child,
                Name = "Subgrupo",
                IsGroup = true,
                LinkedParentJid = Parent,
                IsCommunityAnnounce = true,
                LastMessage = "new",
                LastMessageKind = ChatPreviewKind.Text,
                LastMessageTimestampUtc = System.DateTime.UtcNow
            };

            Assert.True(HistoryChatPreviewApplier.ApplyIfNewer(preview, target));
            Assert.Equal(Parent, target.LinkedParentJid);
            Assert.True(target.IsCommunityAnnounce);
        }
    }
}
