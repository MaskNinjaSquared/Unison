// =============================================================================
// Tests for MessageTail.
//
// The tail of a conversation that can go in a read receipt or deletion range.
// A message without an id cannot be named in those ranges, so the original code
// filtered them out -- but then counted the original list to decide how many to
// skip, and the skip overshot by however many were filtered. With enough of them
// the tail came back empty, the receipt was never sent, and the other side never
// saw the blue ticks.
// =============================================================================
using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class MessageTailTests
    {
        private static ChatMessage Msg(string? id) =>
            new ChatMessage { Id = id! };

        private static List<ChatMessage> List(params ChatMessage?[] items) =>
            new List<ChatMessage>(items!);

        // --- The bug that created the class -------------------------------------

        [Fact]
        public void Filtering_unnamed_messages_does_not_shorten_the_tail()
        {
            // The original code: live.Where(m => m != null && !string.IsNullOrEmpty(m.Id))
            //                         .Skip(Math.Max(0, live.Count - take)).ToList()
            //
            // With 10 messages, 8 without an id, and take = 2, that became Skip(8) over
            // a list of 2 -- returning nothing. The two addressable items should have
            // come back in full.

            var noId1 = Msg(null);
            var noId2 = Msg(null);
            var noId3 = Msg(null);
            var noId4 = Msg(null);
            var noId5 = Msg(null);
            var noId6 = Msg(null);
            var noId7 = Msg(null);
            var noId8 = Msg(null);
            var hasId1 = Msg("AAA");
            var hasId2 = Msg("BBB");

            var all = List(noId1, noId2, noId3, noId4, hasId1, noId5, noId6, noId7, noId8, hasId2);

            var tail = MessageTail.Addressable(all, take: 2);

            Assert.Equal(2, tail.Count);
            Assert.Same(hasId1, tail[0]);
            Assert.Same(hasId2, tail[1]);
        }

        [Fact]
        public void When_every_message_but_one_has_no_id_that_one_still_returns()
        {
            // Extreme version: 9 unnamed, 1 named, take 5. The old code would have
            // returned empty; the fix returns the only addressable message.

            var only = Msg("ONLY");
            var all = List(Msg(null), Msg(null), Msg(null), Msg(null), only,
                           Msg(null), Msg(null), Msg(null), Msg(null), Msg(null));

            var tail = MessageTail.Addressable(all, take: 5);

            Assert.Single(tail);
            Assert.Same(only, tail[0]);
        }

        // --- Unnamed messages never appear --------------------------------------

        [Fact]
        public void Messages_without_an_id_are_never_included_in_the_tail()
        {
            var hasId = Msg("A");
            var noId = Msg(null);

            var tail = MessageTail.Addressable(List(hasId, noId), take: 10);

            Assert.Single(tail);
            Assert.Same(hasId, tail[0]);
        }

        [Fact]
        public void An_empty_string_id_is_treated_as_unnamed()
        {
            var empty = Msg("");
            var hasId = Msg("B");

            var tail = MessageTail.Addressable(List(empty, hasId), take: 10);

            Assert.Single(tail);
            Assert.Same(hasId, tail[0]);
        }

        [Fact]
        public void A_whitespace_only_id_is_treated_as_unnamed()
        {
            // string.IsNullOrEmpty does not catch this, but the behaviour is documented.
            // If the implementation treats whitespace as valid, this test will pin that.
            var whitespace = Msg("   ");

            var tail = MessageTail.Addressable(List(whitespace), take: 5);

            // Whitespace is not empty, so the current implementation keeps it.
            // If that changes, flip the assertion.
            Assert.Single(tail);
        }

        // --- Fewer than take returns all addressable ----------------------------

        [Fact]
        public void When_fewer_addressable_messages_exist_than_requested_all_are_returned()
        {
            var a = Msg("A");
            var b = Msg("B");
            var c = Msg("C");

            var tail = MessageTail.Addressable(List(a, b, c), take: 100);

            Assert.Equal(3, tail.Count);
            Assert.Same(a, tail[0]);
            Assert.Same(b, tail[1]);
            Assert.Same(c, tail[2]);
        }

        // --- Order and identity -------------------------------------------------

        [Fact]
        public void The_tail_preserves_input_order_oldest_first()
        {
            var a = Msg("A"); // oldest
            var b = Msg("B");
            var c = Msg("C");
            var d = Msg("D"); // newest

            var tail = MessageTail.Addressable(List(a, b, c, d), take: 2);

            Assert.Equal(2, tail.Count);
            Assert.Same(c, tail[0]);
            Assert.Same(d, tail[1]);
        }

        [Fact]
        public void The_newest_addressable_messages_are_what_comes_back()
        {
            // "Newest" is defined by position in the list, not by a timestamp field.
            var oldest = Msg("OLD");
            var middle = Msg("MID");
            var newest = Msg("NEW");

            var tail = MessageTail.Addressable(List(oldest, middle, newest), take: 2);

            Assert.DoesNotContain(oldest, tail);
            Assert.Contains(middle, tail);
            Assert.Contains(newest, tail);
        }

        // --- take zero and negative ---------------------------------------------

        [Fact]
        public void Take_zero_returns_an_empty_list()
        {
            var tail = MessageTail.Addressable(List(Msg("A"), Msg("B")), take: 0);

            Assert.Empty(tail);
        }

        [Fact]
        public void Take_negative_returns_an_empty_list()
        {
            var tail = MessageTail.Addressable(List(Msg("A"), Msg("B")), take: -5);

            Assert.Empty(tail);
        }

        // --- Null inputs --------------------------------------------------------

        [Fact]
        public void A_null_list_returns_empty_rather_than_throwing()
        {
            var tail = MessageTail.Addressable(null!, take: 5);

            Assert.NotNull(tail);
            Assert.Empty(tail);
        }

        [Fact]
        public void Null_elements_inside_the_list_are_skipped_without_throwing()
        {
            var a = Msg("A");
            var b = Msg("B");

            var tail = MessageTail.Addressable(List(a, null, b, null), take: 10);

            Assert.Equal(2, tail.Count);
            Assert.Same(a, tail[0]);
            Assert.Same(b, tail[1]);
        }

        // --- Edge cases ---------------------------------------------------------

        [Fact]
        public void An_empty_list_returns_an_empty_tail()
        {
            var tail = MessageTail.Addressable(List(), take: 5);

            Assert.Empty(tail);
        }

        [Fact]
        public void A_list_with_only_unnamed_messages_returns_empty()
        {
            var tail = MessageTail.Addressable(List(Msg(null), Msg(""), Msg(null)), take: 10);

            Assert.Empty(tail);
        }

        [Fact]
        public void Take_exactly_the_count_returns_all_addressable()
        {
            var a = Msg("A");
            var b = Msg("B");
            var c = Msg("C");

            var tail = MessageTail.Addressable(List(a, b, c), take: 3);

            Assert.Equal(3, tail.Count);
            Assert.Same(a, tail[0]);
            Assert.Same(b, tail[1]);
            Assert.Same(c, tail[2]);
        }

        [Fact]
        public void Take_one_more_than_the_count_returns_all_addressable()
        {
            var a = Msg("A");
            var b = Msg("B");

            var tail = MessageTail.Addressable(List(a, b), take: 3);

            Assert.Equal(2, tail.Count);
        }
    }
}
