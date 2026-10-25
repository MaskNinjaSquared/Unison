using System;
using System.Collections.Generic;
using Unison.Core.Helpers.Chats;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ChatListRowOrderTests
    {
        private static DateTime Utc(int day) =>
            new DateTime(2026, 1, day, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void TargetIndex_moves_freshest_tip_to_top_below_pins()
        {
            var pinned = new ChatItem
            {
                Name = "pinned",
                IsChatPinned = true,
                PinnedTimestamp = 1,
                LastMessageTimestampUtc = Utc(1)
            };
            var older = new ChatItem { Name = "older", LastMessageTimestampUtc = Utc(10) };
            var bumped = new ChatItem { Name = "bumped", LastMessageTimestampUtc = Utc(5) };
            var visible = new List<ChatItem> { pinned, older, bumped };

            bumped.LastMessageTimestampUtc = Utc(20);

            Assert.Equal(1, ChatListRowOrder.TargetIndex(visible, bumped));
        }

        [Fact]
        public void TargetIndex_no_op_when_already_correct()
        {
            var a = new ChatItem { Name = "a", LastMessageTimestampUtc = Utc(20) };
            var b = new ChatItem { Name = "b", LastMessageTimestampUtc = Utc(10) };
            var visible = new List<ChatItem> { a, b };

            Assert.Equal(0, ChatListRowOrder.TargetIndex(visible, a));
        }

        [Fact]
        public void TargetIndex_orders_by_catalogue_probe_when_visible_alias_is_stale()
        {
            var older = new ChatItem { Name = "older", LastMessageTimestampUtc = Utc(10) };
            var visibleAlias = new ChatItem
            {
                Name = "alias",
                JID = "pn@s.whatsapp.net",
                LastMessageTimestampUtc = Utc(5)
            };
            var catalogue = new ChatItem
            {
                Name = "alias",
                JID = "lid@lid",
                LastMessageTimestampUtc = Utc(20)
            };
            var visible = new List<ChatItem> { older, visibleAlias };

            Assert.Equal(0, ChatListRowOrder.TargetIndex(visible, visibleAlias, catalogue));
        }
    }
}
