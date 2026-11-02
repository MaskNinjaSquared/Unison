using System;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class CommunityHubApplyTipIfNewerTests
    {
        [Fact]
        public void ApplyTipIfNewer_copies_fresher_member_onto_hub()
        {
            var hub = new ChatItem
            {
                JID = "120363999@g.us",
                IsCommunityParent = true,
                LastMessageTimestampUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                LastMessageId = "old"
            };
            var member = new ChatItem
            {
                JID = "120363111@g.us",
                Name = "Geral",
                LinkedParentJid = hub.JID,
                LastMessage = "oi",
                LastMessageAuthor = "Alice",
                LastMessageTimestampUtc = new DateTime(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc),
                LastMessageId = "new"
            };

            Assert.True(CommunityHubPresentation.ApplyTipIfNewer(hub, member));
            Assert.Equal("oi", hub.LastMessage);
            Assert.Equal("Geral", hub.LastMessageAuthor);
            Assert.Equal("Alice", hub.LastPreview.AuthorDetail);
            Assert.True(hub.LastPreview.ShowAuthorChevron);
            Assert.Equal(member.LastMessageTimestampUtc, hub.LastMessageTimestampUtc);
        }

        [Fact]
        public void ApplyTipIfNewer_rejects_stale_member()
        {
            var hub = new ChatItem
            {
                JID = "120363999@g.us",
                LastMessageTimestampUtc = new DateTime(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc),
                LastMessageId = "new"
            };
            var member = new ChatItem
            {
                JID = "120363111@g.us",
                LastMessageTimestampUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                LastMessageId = "old"
            };

            Assert.False(CommunityHubPresentation.ApplyTipIfNewer(hub, member));
            Assert.Equal("new", hub.LastMessageId);
        }
    }
}
