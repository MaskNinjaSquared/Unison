// =============================================================================
// Tests for ActiveChatMessageWindow.
//
// The open conversation keeps a bounded slice of its timeline in memory; the
// rest stays in SQLite. What falls out of the slice disappears from the screen
// until the user scrolls back for it, so the rule for what stays is visible.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ActiveChatMessageWindowTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime Now = T0.AddDays(1);

        private static ChatMessage At(string id, int minutes) =>
            new ChatMessage { Id = id, Timestamp = T0.AddMinutes(minutes) };

        private static ChatMessage Pinned(string id, int minutes, DateTime? expires = null) =>
            new ChatMessage { Id = id, Timestamp = T0.AddMinutes(minutes), IsPinned = true, PinExpiresAtUtc = expires };

        private static List<string?> Ids(List<ChatMessage> messages) => messages.Select(m => m?.Id).ToList();

        [Fact]
        public void A_window_within_capacity_is_left_exactly_as_it_was()
        {
            var a = At("a", 2);
            var b = At("b", 1);
            var messages = new List<ChatMessage> { a, b };

            bool trimmed = ActiveChatMessageWindow.TrimInPlace(messages, 2, Now);

            Assert.False(trimmed);
            Assert.Same(a, messages[0]);
            Assert.Same(b, messages[1]);
        }

        [Fact]
        public void Over_capacity_keeps_the_newest_messages_in_timeline_order()
        {
            var messages = new List<ChatMessage> { At("a", 1), At("b", 2), At("c", 3), At("d", 4) };

            bool trimmed = ActiveChatMessageWindow.TrimInPlace(messages, 2, Now);

            Assert.True(trimmed);
            Assert.Equal(new[] { "c", "d" }, Ids(messages));
        }

        [Fact]
        public void A_live_pin_stays_even_when_it_is_older_than_everything_kept()
        {
            // The pinned banner reads from this list; trimming the pin away would make
            // it vanish from the top of the chat while it is still pinned.
            var messages = new List<ChatMessage> { Pinned("p", 0), At("a", 1), At("b", 2), At("c", 3) };

            ActiveChatMessageWindow.TrimInPlace(messages, 2, Now);

            Assert.Equal(new[] { "p", "c" }, Ids(messages));
        }

        [Fact]
        public void An_expired_pin_competes_like_any_other_message()
        {
            var messages = new List<ChatMessage> { Pinned("p", 0, expires: Now.AddMinutes(-1)), At("a", 1), At("b", 2) };

            ActiveChatMessageWindow.TrimInPlace(messages, 2, Now);

            Assert.Equal(new[] { "a", "b" }, Ids(messages));
        }

        [Fact]
        public void A_pin_with_a_future_expiry_is_still_live()
        {
            var messages = new List<ChatMessage> { Pinned("p", 0, expires: Now.AddHours(1)), At("a", 1), At("b", 2) };

            ActiveChatMessageWindow.TrimInPlace(messages, 2, Now);

            Assert.Equal(new[] { "p", "b" }, Ids(messages));
        }

        [Fact]
        public void Pins_beyond_capacity_are_all_kept_and_leave_no_room_for_the_rest()
        {
            var messages = new List<ChatMessage> { Pinned("p1", 0), Pinned("p2", 1), Pinned("p3", 2), At("a", 3) };

            ActiveChatMessageWindow.TrimInPlace(messages, 2, Now);

            Assert.Equal(new[] { "p1", "p2", "p3" }, Ids(messages));
        }

        [Fact]
        public void Newest_is_decided_on_comparable_utc_not_on_the_raw_clock()
        {
            // Rows read back from SQLite are Unspecified and already UTC wall-clock. A raw
            // comparison against a live Utc message is off by the local offset, and the
            // newer one could be the one dropped.
            var fromSqlite = new ChatMessage
            {
                Id = "sqlite",
                Timestamp = DateTime.SpecifyKind(T0.AddMinutes(10), DateTimeKind.Unspecified)
            };
            var live = new ChatMessage { Id = "live", Timestamp = T0.AddMinutes(5) };
            var old = new ChatMessage { Id = "old", Timestamp = T0 };
            var messages = new List<ChatMessage> { live, fromSqlite, old };

            ActiveChatMessageWindow.TrimInPlace(messages, 2, Now);

            Assert.Equal(new[] { "live", "sqlite" }, Ids(messages));
        }

        [Fact]
        public void The_same_id_twice_is_kept_once()
        {
            var messages = new List<ChatMessage> { At("a", 1), At("b", 2), At("b", 2), At("c", 3) };

            ActiveChatMessageWindow.TrimInPlace(messages, 3, Now);

            Assert.Equal(new[] { "a", "b", "c" }, Ids(messages));
        }

        [Fact]
        public void A_pin_without_an_id_is_not_kept_twice()
        {
            // It is both a live pin and, lacking an id to exclude it by, a regular message.
            var orphanPin = new ChatMessage { Timestamp = T0.AddMinutes(9), IsPinned = true };
            var messages = new List<ChatMessage> { At("a", 1), At("b", 2), orphanPin };

            ActiveChatMessageWindow.TrimInPlace(messages, 2, Now);

            Assert.Equal(1, messages.Count(m => ReferenceEquals(m, orphanPin)));
            Assert.Equal(2, messages.Count);
        }

        [Fact]
        public void Distinct_messages_without_ids_are_not_collapsed_into_one()
        {
            var first = new ChatMessage { Timestamp = T0.AddMinutes(5) };
            var second = new ChatMessage { Timestamp = T0.AddMinutes(6) };
            var messages = new List<ChatMessage> { At("a", 1), first, second };

            ActiveChatMessageWindow.TrimInPlace(messages, 2, Now);

            Assert.Equal(new[] { first, second }, messages);
        }

        [Fact]
        public void Null_entries_do_not_survive_a_trim()
        {
            var messages = new List<ChatMessage> { At("a", 1), null!, At("b", 2), At("c", 3) };

            ActiveChatMessageWindow.TrimInPlace(messages, 2, Now);

            Assert.Equal(new[] { "b", "c" }, Ids(messages));
        }

        [Fact]
        public void A_missing_list_is_not_an_error()
        {
            Assert.False(ActiveChatMessageWindow.TrimInPlace(null!, 2, Now));
        }
    }
}
