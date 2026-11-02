using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Models
{
    public sealed class CommunityJoinableMembersTests
    {
        [Fact]
        public void ReplaceCommunityJoinableGroups_sets_flag_and_rows()
        {
            var hub = new ChatItem
            {
                JID = "120363999@g.us",
                IsCommunityParent = true
            };
            hub.SetCommunityGroups(new List<ChatItem>
            {
                new ChatItem
                {
                    JID = "120363111@g.us",
                    Name = "Avisos",
                    IsCommunityAnnounce = true,
                    LinkedParentJid = hub.JID
                }
            });

            var joinable = new List<ChatItem>
            {
                new ChatItem
                {
                    JID = "120363222@g.us",
                    Name = "Geral",
                    CanJoin = true,
                    LinkedParentJid = hub.JID
                }
            };

            hub.ReplaceCommunityJoinableGroups(
                CommunityHubPresentation.OrderMembers(joinable));

            Assert.True(hub.HasCommunityJoinableMembers);
            Assert.Single(hub.CommunityJoinableMembers);
            Assert.True(hub.CommunityJoinableMembers[0].CanJoin);
            Assert.Single(hub.CommunityMembers);
            Assert.False(hub.CommunityMembers[0].CanJoin);
        }

        [Fact]
        public void ClearCommunityGroups_also_clears_joinable()
        {
            var hub = new ChatItem { JID = "120363999@g.us" };
            hub.ReplaceCommunityJoinableGroups(new List<ChatItem>
            {
                new ChatItem { JID = "120363222@g.us", CanJoin = true }
            });

            hub.ClearCommunityGroups();

            Assert.False(hub.HasCommunityJoinableMembers);
            Assert.Empty(hub.CommunityJoinableMembers);
        }
    }
}
