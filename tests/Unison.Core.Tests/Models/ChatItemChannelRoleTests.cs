// =============================================================================
// Channel composer: only the owner and admins post; followers read.
// Baileys NewsletterViewRole: OWNER | ADMIN | SUBSCRIBER | GUEST.
// =============================================================================
using System.Collections.Generic;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Models
{
    public sealed class ChatItemChannelRoleTests
    {
        private const string ChannelJid = "120363000000000010@newsletter";

        [Theory]
        [InlineData(ChannelViewerRole.Owner)]
        [InlineData(ChannelViewerRole.Admin)]
        public void Owner_and_admin_can_post(ChannelViewerRole role)
        {
            var chat = new ChatItem { JID = ChannelJid, Kind = ChatKind.Channel, ChannelRole = role };

            Assert.True(chat.CanPostToChannel);
            Assert.False(chat.IsComposerLocked);
        }

        [Theory]
        [InlineData(ChannelViewerRole.Unknown)]
        [InlineData(ChannelViewerRole.Subscriber)]
        [InlineData(ChannelViewerRole.Guest)]
        public void Followers_and_unknown_role_stay_read_only(ChannelViewerRole role)
        {
            var chat = new ChatItem { JID = ChannelJid, Kind = ChatKind.Channel, ChannelRole = role };

            Assert.False(chat.CanPostToChannel);
            Assert.True(chat.IsComposerLocked);
        }

        [Fact]
        public void Role_on_a_non_channel_does_not_grant_posting()
        {
            var chat = new ChatItem
            {
                JID = "120363000000000003@g.us",
                Kind = ChatKind.Group,
                ChannelRole = ChannelViewerRole.Owner
            };

            Assert.False(chat.CanPostToChannel);
        }

        [Fact]
        public void Role_change_notifies_the_composer_lock()
        {
            var chat = new ChatItem { JID = ChannelJid, Kind = ChatKind.Channel };
            var raised = new List<string>();
            chat.PropertyChanged += (s, e) => raised.Add(e.PropertyName);

            chat.ChannelRole = ChannelViewerRole.Admin;

            Assert.Contains(nameof(ChatItem.ChannelRole), raised);
            Assert.Contains(nameof(ChatItem.CanPostToChannel), raised);
            Assert.Contains(nameof(ChatItem.IsComposerLocked), raised);
        }
    }
}
