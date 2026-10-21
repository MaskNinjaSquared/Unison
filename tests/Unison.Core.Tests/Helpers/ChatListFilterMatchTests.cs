// =============================================================================
// Chat list filter predicates — Groups must not keep community hubs; Communities
// keeps hubs only. Mirrors MatchesActiveFilter in ChatListViewModel.
// =============================================================================
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ChatListFilterMatchTests
    {
        [Fact]
        public void Groups_excludes_community_hubs()
        {
            var hub = new ChatItem
            {
                JID = "120363@g.us",
                IsGroup = true,
                IsCommunityParent = true,
                Kind = ChatKind.Community
            };
            var group = new ChatItem
            {
                JID = "120364@g.us",
                IsGroup = true,
                Kind = ChatKind.Group
            };

            Assert.False(Matches(ChatListFilter.Groups, hub));
            Assert.True(Matches(ChatListFilter.Groups, group));
        }

        [Fact]
        public void Communities_keeps_hubs_only()
        {
            var hub = new ChatItem
            {
                JID = "120363@g.us",
                IsGroup = true,
                IsCommunityParent = true,
                Kind = ChatKind.Community
            };
            var linked = new ChatItem
            {
                JID = "120364@g.us",
                IsGroup = true,
                LinkedParentJid = "120363@g.us",
                Kind = ChatKind.Group
            };
            var direct = new ChatItem
            {
                JID = "5511999999999@s.whatsapp.net",
                Kind = ChatKind.Direct
            };

            Assert.True(Matches(ChatListFilter.Communities, hub));
            Assert.False(Matches(ChatListFilter.Communities, linked));
            Assert.False(Matches(ChatListFilter.Communities, direct));
        }

        /// <summary>Same rules as ChatListViewModel.MatchesActiveFilter.</summary>
        private static bool Matches(ChatListFilter filter, ChatItem chat)
        {
            if (chat == null)
            {
                return false;
            }

            switch (filter)
            {
                case ChatListFilter.All:
                    return true;
                case ChatListFilter.Unread:
                    return chat.HasUnread;
                case ChatListFilter.Favorites:
                    return chat.IsFavorite;
                case ChatListFilter.Contacts:
                    return !chat.IsGroup;
                case ChatListFilter.NonContacts:
                    return chat.IsDirect;
                case ChatListFilter.Groups:
                    return chat.IsGroup && !chat.IsCommunity && !chat.IsCommunityParent;
                case ChatListFilter.Drafts:
                    return chat.HasDraft;
                case ChatListFilter.Communities:
                    return chat.IsCommunity || chat.IsCommunityParent;
                default:
                    return true;
            }
        }
    }
}
