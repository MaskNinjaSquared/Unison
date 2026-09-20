// =============================================================================
// Tests for ChatUnreadTally.
//
// The rule these cover used to exist twice in WhatsAppService.IncomingPump --
// once for a single arriving message and once for a batch replayed after a
// reconnect. Both copies did the same thing, which is the shape that produced a
// real drift once already in this refactor, so they are now one function.
// =============================================================================
using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ChatUnreadTallyTests
    {
        private static ChatItem Row(int unread)
        {
            return new ChatItem { JID = "5511988887777@s.whatsapp.net", UnreadCount = unread };
        }

        // --- Reading the current count -----------------------------------------

        [Fact]
        public void With_no_rows_the_count_is_zero()
        {
            Assert.Equal(0, ChatUnreadTally.HighestAmong(new List<ChatItem>()));
            Assert.Equal(0, ChatUnreadTally.HighestAmong(null!));
        }

        [Fact]
        public void The_highest_row_decides_not_the_first_one()
        {
            // A row that missed a mutation is stale, not authoritative. Taking the lower
            // value here would silently drop unread messages.
            var rows = new List<ChatItem> { Row(2), Row(7), Row(1) };

            Assert.Equal(7, ChatUnreadTally.HighestAmong(rows));
        }

        [Fact]
        public void A_negative_count_never_leaks_out()
        {
            Assert.Equal(0, ChatUnreadTally.HighestAmong(new List<ChatItem> { Row(-3) }));
        }

        [Fact]
        public void A_hole_in_the_list_is_stepped_over()
        {
            var rows = new List<ChatItem> { null!, Row(4), null! };

            Assert.Equal(4, ChatUnreadTally.HighestAmong(rows));
        }

        // --- Incrementing -------------------------------------------------------

        [Fact]
        public void One_arriving_message_adds_one()
        {
            var chat = Row(3);

            Assert.Equal(4, ChatUnreadTally.Bump(chat, new List<ChatItem> { chat }, 1));
            Assert.Equal(4, chat.UnreadCount);
        }

        [Fact]
        public void A_replayed_batch_adds_all_of_it_at_once()
        {
            // After a reconnect the missed messages arrive as one summary carrying a count,
            // not as five separate bumps.
            var chat = Row(2);

            Assert.Equal(7, ChatUnreadTally.Bump(chat, new List<ChatItem> { chat }, 5));
        }

        [Fact]
        public void Every_row_of_the_conversation_ends_up_showing_the_same_number()
        {
            // This is the whole point. The same conversation is listed under its PN and its
            // LID form; whichever row the list renders has to show the same badge.
            var pn = Row(3);
            var lid = Row(1);

            ChatUnreadTally.Bump(pn, new List<ChatItem> { pn, lid }, 1);

            Assert.Equal(4, pn.UnreadCount);
            Assert.Equal(4, lid.UnreadCount);
        }

        [Fact]
        public void A_stale_sibling_row_cannot_drag_the_count_backwards()
        {
            // The row the caller happens to be holding is behind the other one. Starting
            // from it would turn an increment into a decrease.
            var behind = Row(0);
            var ahead = Row(6);

            Assert.Equal(7, ChatUnreadTally.Bump(behind, new List<ChatItem> { behind, ahead }, 1));
            Assert.Equal(7, behind.UnreadCount);
            Assert.Equal(7, ahead.UnreadCount);
        }

        [Fact]
        public void With_no_sibling_rows_the_held_row_is_the_starting_point()
        {
            // A conversation that has just been created has no rows indexed yet.
            var chat = Row(2);

            Assert.Equal(3, ChatUnreadTally.Bump(chat, new List<ChatItem>(), 1));
            Assert.Equal(3, chat.UnreadCount);
        }

        [Fact]
        public void The_siblings_decide_even_when_the_held_row_is_not_among_them()
        {
            // Recorded: once any sibling row exists, the held row's own count is not part
            // of the calculation -- it is only written to.
            var held = Row(10);
            var sibling = Row(2);

            Assert.Equal(3, ChatUnreadTally.Bump(held, new List<ChatItem> { sibling }, 1));
            Assert.Equal(3, held.UnreadCount);
        }

        [Fact]
        public void A_count_that_had_gone_negative_is_repaired_on_the_next_message()
        {
            var chat = Row(-5);

            Assert.Equal(1, ChatUnreadTally.Bump(chat, new List<ChatItem>(), 1));
        }

        [Fact]
        public void The_result_never_goes_below_zero()
        {
            var chat = Row(1);

            Assert.Equal(0, ChatUnreadTally.Bump(chat, new List<ChatItem> { chat }, -10));
        }

        [Fact]
        public void Nothing_to_write_to_is_not_a_crash()
        {
            Assert.Equal(1, ChatUnreadTally.Bump(null!, null!, 1));
        }
    }
}
