// =============================================================================
// Tests for LiveIncomingStripCoalesce.
// =============================================================================
using System;
using Unison.Core.Helpers.Chats;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class LiveIncomingStripCoalesceTests
    {
        [Fact]
        public void Merge_sums_unread_and_keeps_newer_tip()
        {
            var target = new LiveIncomingChatListApplyRequest
            {
                ChatJid = "120363@g.us",
                PreviewText = "old",
                Timestamp = new DateTime(2026, 11, 8, 1, 0, 0, DateTimeKind.Utc),
                UnreadDelta = 1,
                MessageId = "a"
            };
            var incoming = new LiveIncomingChatListApplyRequest
            {
                ChatJid = "120363@g.us",
                PreviewText = "new",
                Timestamp = new DateTime(2026, 11, 8, 1, 0, 5, DateTimeKind.Utc),
                CountsAsUnread = true,
                MessageId = "b",
                HasCitation = true
            };

            LiveIncomingStripCoalesce.Merge(target, incoming);

            Assert.Equal(2, target.UnreadDelta);
            Assert.True(target.CountsAsUnread);
            Assert.Equal("new", target.PreviewText);
            Assert.Equal("b", target.MessageId);
            Assert.True(target.HasCitation);
        }

        [Fact]
        public void Merge_ignores_older_tip_but_still_adds_unread()
        {
            var newer = new DateTime(2026, 11, 8, 1, 0, 10, DateTimeKind.Utc);
            var target = new LiveIncomingChatListApplyRequest
            {
                PreviewText = "kept",
                Timestamp = newer,
                UnreadDelta = 3,
                MessageId = "kept-id"
            };
            var incoming = new LiveIncomingChatListApplyRequest
            {
                PreviewText = "stale",
                Timestamp = newer.AddSeconds(-30),
                UnreadDelta = 2,
                MessageId = "stale-id"
            };

            LiveIncomingStripCoalesce.Merge(target, incoming);

            Assert.Equal(5, target.UnreadDelta);
            Assert.Equal("kept", target.PreviewText);
            Assert.Equal("kept-id", target.MessageId);
        }

        [Theory]
        [InlineData(0, false, 0)]
        [InlineData(0, true, 1)]
        [InlineData(4, false, 4)]
        [InlineData(4, true, 4)]
        public void EffectiveUnread_honors_legacy_counts_flag(
            int unreadDelta,
            bool countsAsUnread,
            int expected)
        {
            var request = new LiveIncomingChatListApplyRequest
            {
                UnreadDelta = unreadDelta,
                CountsAsUnread = countsAsUnread
            };

            Assert.Equal(expected, LiveIncomingStripCoalesce.EffectiveUnread(request));
        }
    }
}
