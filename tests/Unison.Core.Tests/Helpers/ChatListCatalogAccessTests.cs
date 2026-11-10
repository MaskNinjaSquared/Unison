// =============================================================================
// ChatListCatalogAccess — scope predicates and frugal/desktop attach policies.
// =============================================================================
using Unison.Core.Helpers.Chats;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ChatListCatalogAccessTests
    {
        [Theory]
        [InlineData(ChatListScope.Active, true)]
        [InlineData(ChatListScope.Channels, false)]
        [InlineData(ChatListScope.Archived, false)]
        public void Community_projection_only_for_active_scope(
            ChatListScope scope,
            bool expected)
        {
            Assert.Equal(expected, ChatListCatalogAccess.NeedsCommunityProjection(scope));
        }

        [Theory]
        [InlineData(true, true, ChatListScope.Active, true)]
        [InlineData(true, true, ChatListScope.Channels, false)]
        [InlineData(true, true, ChatListScope.Archived, false)]
        [InlineData(true, false, ChatListScope.Active, false)]
        [InlineData(false, true, ChatListScope.Active, false)]
        public void Defer_attach_only_frugal_enriching_active(
            bool preferFrugal,
            bool enriching,
            ChatListScope scope,
            bool expected)
        {
            Assert.Equal(
                expected,
                ChatListCatalogAccess.ShouldDeferAttachRefresh(preferFrugal, enriching, scope));
        }

        [Fact]
        public void MatchesScope_channels_keeps_active_newsletters_only()
        {
            var channel = new ChatItem
            {
                JID = "120363@newsletter",
                Kind = ChatKind.Channel,
                Status = ChatStatus.Active
            };
            var archivedChannel = new ChatItem
            {
                JID = "120364@newsletter",
                Kind = ChatKind.Channel,
                Status = ChatStatus.Archived
            };
            var dm = new ChatItem
            {
                JID = "5511999990001@s.whatsapp.net",
                Kind = ChatKind.Direct,
                Status = ChatStatus.Active
            };

            Assert.True(ChatListCatalogAccess.MatchesScope(channel, ChatListScope.Channels));
            Assert.False(ChatListCatalogAccess.MatchesScope(archivedChannel, ChatListScope.Channels));
            Assert.False(ChatListCatalogAccess.MatchesScope(dm, ChatListScope.Channels));
            Assert.False(ChatListCatalogAccess.MatchesScope(channel, ChatListScope.Active));
            Assert.True(ChatListCatalogAccess.MatchesScope(dm, ChatListScope.Active));
            Assert.True(ChatListCatalogAccess.MatchesScope(archivedChannel, ChatListScope.Archived));
        }
    }
}
