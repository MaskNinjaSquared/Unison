// =============================================================================
// A freshly created chat must sit at the top of the list before any message tip
// arrives. Without a LastMessageTimestampUtc the sort buries it under MinValue.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using Unison.Core.Helpers.Chats;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class NewChatListSeedTests
    {
        [Fact]
        public void Build_uses_the_display_name_and_stamps_now()
        {
            DateTime now = new DateTime(2026, 11, 6, 19, 0, 0, DateTimeKind.Utc);

            ChatItem seed = NewChatListSeed.Build(
                "120363000000000003@g.us",
                "  Família  ",
                ChatKind.Group,
                now,
                yesterdayLabel: "Yesterday");

            Assert.Equal("120363000000000003@g.us", seed.JID);
            Assert.Equal("Família", seed.Name);
            Assert.Equal(ChatKind.Group, seed.Kind);
            Assert.Equal(ChatStatus.Active, seed.Status);
            Assert.Equal(now, seed.LastMessageTimestampUtc);
            Assert.Equal(string.Empty, seed.LastMessage);
            Assert.False(string.IsNullOrEmpty(seed.Timestamp));
        }

        [Fact]
        public void Build_sorts_above_an_older_tip()
        {
            DateTime now = new DateTime(2026, 11, 6, 19, 0, 0, DateTimeKind.Utc);
            ChatItem seed = NewChatListSeed.Build(
                "120363000000000003@g.us",
                "Família",
                ChatKind.Group,
                now,
                "Yesterday");
            var older = new ChatItem
            {
                JID = "a@s.whatsapp.net",
                Name = "Older",
                LastMessageTimestampUtc = now.AddDays(-1)
            };

            List<ChatItem> ordered = new[] { older, seed }
                .OrderBy(c => c, Comparer<ChatItem>.Create(ChatListRowOrder.Compare))
                .ToList();

            Assert.Same(seed, ordered[0]);
        }

        [Fact]
        public void BuildCommunityHub_marks_the_row_as_a_community_parent()
        {
            DateTime now = new DateTime(2026, 11, 6, 19, 0, 0, DateTimeKind.Utc);

            ChatItem hub = NewChatListSeed.BuildCommunityHub(
                "120363000000000004@g.us",
                " Bairro ",
                ChatKind.Group,
                now,
                "Yesterday");

            Assert.True(hub.IsCommunityParent);
            Assert.Equal("Bairro", hub.Name);
            Assert.Equal(now, hub.LastMessageTimestampUtc);
            Assert.Equal(ChatStatus.Active, hub.Status);
        }
    }
}
