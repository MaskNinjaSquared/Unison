// =============================================================================
// Tests for DuplicateChatRowMerge.
//
// The startup and post-sync dedupe pass collapses every set of rows that resolve
// to one canonical address. One row survives; the others are removed after
// handing over what they knew. Nothing reports a field taken from the wrong side.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class DuplicateChatRowMergeTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);

        private static ChatItem Row(string jid, string? avatar = null) =>
            new ChatItem { JID = jid, AvatarUrl = avatar };

        private static ChatItem Preview(string? text, DateTime? at, string clock = "12:00", string? id = null) =>
            new ChatItem { LastMessage = text, Timestamp = clock, LastMessageTimestampUtc = at, LastMessageId = id };

        private static void Absorb(ChatItem primary, ChatItem secondary) =>
            DuplicateChatRowMerge.Absorb(primary, secondary, DateTime.MinValue, DateTime.MinValue);

        // --- Which row survives ------------------------------------------------

        private static List<ChatItem> Order(IEnumerable<ChatItem> rows, Dictionary<ChatItem, int>? counts = null) =>
            DuplicateChatRowMerge.OrderForMerge(
                rows,
                row => row.JID.EndsWith("@lid", StringComparison.OrdinalIgnoreCase),
                row => counts != null && counts.TryGetValue(row, out int n) ? n : 0);

        [Fact]
        public void A_phone_addressed_row_beats_a_lid_row_whatever_else_the_lid_row_has()
        {
            var lid = Row("1@lid", avatar: "ms-appdata:///a.jpg");
            var phone = Row("55@s.whatsapp.net");

            var ordered = Order(new[] { lid, phone }, new Dictionary<ChatItem, int> { [lid] = 50 });

            Assert.Same(phone, ordered[0]);
        }

        [Fact]
        public void Between_two_phone_rows_the_one_with_a_picture_wins()
        {
            var bare = Row("55@s.whatsapp.net");
            var pictured = Row("55@s.whatsapp.net", avatar: "ms-appdata:///a.jpg");

            Assert.Same(pictured, Order(new[] { bare, pictured })[0]);
        }

        [Fact]
        public void With_identity_and_picture_tied_more_cached_messages_wins()
        {
            var few = Row("55@s.whatsapp.net");
            var many = Row("55@s.whatsapp.net");

            var ordered = Order(new[] { few, many }, new Dictionary<ChatItem, int> { [few] = 1, [many] = 9 });

            Assert.Same(many, ordered[0]);
        }

        [Fact]
        public void A_full_tie_keeps_list_order()
        {
            var first = Row("55@s.whatsapp.net");
            var second = Row("55@s.whatsapp.net");

            Assert.Equal(new[] { first, second }, Order(new[] { first, second }));
        }

        // --- Preview -----------------------------------------------------------

        [Fact]
        public void A_newer_secondary_preview_replaces_the_primary_one()
        {
            var primary = Preview("old", T0);
            var secondary = Preview("new", T0.AddMinutes(1), clock: "12:01");

            Absorb(primary, secondary);

            Assert.Equal("new", primary.LastMessage);
            Assert.Equal("12:01", primary.Timestamp);
            Assert.Equal(T0.AddMinutes(1), primary.LastMessageTimestampUtc);
        }

        [Fact]
        public void An_older_secondary_preview_is_ignored()
        {
            var primary = Preview("new", T0.AddMinutes(1));
            var secondary = Preview("old", T0);

            Absorb(primary, secondary);

            Assert.Equal("new", primary.LastMessage);
        }

        [Fact]
        public void A_primary_with_nothing_to_show_takes_the_secondary_preview_even_if_older()
        {
            var primary = Preview(null, T0.AddMinutes(5));
            var secondary = Preview("something", T0);

            Absorb(primary, secondary);

            Assert.Equal("something", primary.LastMessage);
        }

        [Fact]
        public void A_blank_secondary_preview_never_wipes_the_primary_one()
        {
            var primary = Preview("keep", T0);
            var secondary = Preview(" ", T0.AddMinutes(9));

            Absorb(primary, secondary);

            Assert.Equal("keep", primary.LastMessage);
        }

        [Fact]
        public void The_preview_fields_travel_together_including_author_and_message_id()
        {
            // Half a preview -- new text over the old author line, or over the id of the
            // message it replaced -- renders a message that was never sent, and
            // ChatPreviewTip only fills a missing id, never a wrong one.
            var primary = Preview("old", T0, id: "OLD");
            primary.LastPreview.Author = "Ana";
            var secondary = Preview("new", T0.AddMinutes(1), id: "NEW");
            secondary.LastPreview.Author = "Bruno";
            secondary.LastMessageKind = ChatPreviewKind.Image;

            Absorb(primary, secondary);

            Assert.Equal("Bruno", primary.LastPreview.Author);
            Assert.Equal("NEW", primary.LastMessageId);
            Assert.Equal(ChatPreviewKind.Image, primary.LastMessageKind);
        }

        [Fact]
        public void A_row_without_a_stamp_is_dated_by_its_newest_cached_message()
        {
            var primary = Preview("primary", null);
            var secondary = Preview("secondary", T0.AddMinutes(1));

            DuplicateChatRowMerge.Absorb(primary, secondary, T0.AddMinutes(5), DateTime.MinValue);

            Assert.Equal("primary", primary.LastMessage);
        }

        [Fact]
        public void Stamps_are_compared_as_comparable_utc()
        {
            // SQLite rows come back Unspecified; a raw compare against a Utc stamp is off by
            // the local offset.
            var primary = Preview("primary", T0.AddMinutes(5));
            var secondary = Preview("secondary", DateTime.SpecifyKind(T0.AddMinutes(10), DateTimeKind.Unspecified));

            Absorb(primary, secondary);

            Assert.Equal("secondary", primary.LastMessage);
        }

        // --- Unread, avatar, pin -----------------------------------------------

        [Fact]
        public void Unread_takes_the_higher_count_rather_than_the_sum()
        {
            var primary = new ChatItem { UnreadCount = 2 };
            var secondary = new ChatItem { UnreadCount = 5 };

            Absorb(primary, secondary);

            Assert.Equal(5, primary.UnreadCount);
        }

        [Fact]
        public void The_secondary_picture_is_taken_only_when_the_primary_has_none()
        {
            var failedAt = T0.AddHours(-1);
            var primary = new ChatItem();
            var secondary = new ChatItem
            {
                AvatarUrl = "ms-appdata:///s.jpg",
                AvatarFetchedAtUtc = T0,
                AvatarFetchFailedAtUtc = failedAt,
                AvatarFetchFailureReason = "timeout"
            };

            Absorb(primary, secondary);

            Assert.Equal("ms-appdata:///s.jpg", primary.AvatarUrl);
            Assert.Equal(T0, primary.AvatarFetchedAtUtc);
            Assert.Equal(failedAt, primary.AvatarFetchFailedAtUtc);
            Assert.Equal("timeout", primary.AvatarFetchFailureReason);

            var pictured = new ChatItem { AvatarUrl = "ms-appdata:///p.jpg" };
            Absorb(pictured, secondary);
            Assert.Equal("ms-appdata:///p.jpg", pictured.AvatarUrl);
        }

        [Fact]
        public void A_pin_on_the_secondary_carries_over_to_an_unpinned_primary()
        {
            // history_chat_preview has no pin columns, so a sync-created primary is unpinned
            // and would otherwise sink the conversation down the list.
            var primary = new ChatItem();
            var secondary = new ChatItem { IsChatPinned = true, PinnedTimestamp = 100 };

            Absorb(primary, secondary);

            Assert.True(primary.IsChatPinned);
            Assert.Equal(100, primary.PinnedTimestamp);
        }

        [Fact]
        public void The_more_recent_pin_wins_when_both_rows_are_pinned()
        {
            var primary = new ChatItem { IsChatPinned = true, PinnedTimestamp = 100 };
            Absorb(primary, new ChatItem { IsChatPinned = true, PinnedTimestamp = 200 });
            Assert.Equal(200, primary.PinnedTimestamp);

            Absorb(primary, new ChatItem { IsChatPinned = true, PinnedTimestamp = 150 });
            Assert.Equal(200, primary.PinnedTimestamp);
        }

        [Fact]
        public void A_pin_stamped_zero_is_an_unpin_and_does_not_travel()
        {
            var primary = new ChatItem();

            Absorb(primary, new ChatItem { IsChatPinned = true, PinnedTimestamp = 0 });

            Assert.False(primary.IsChatPinned);
        }

        [Fact]
        public void Missing_rows_are_not_an_error()
        {
            DuplicateChatRowMerge.Absorb(null!, new ChatItem(), DateTime.MinValue, DateTime.MinValue);
            DuplicateChatRowMerge.Absorb(new ChatItem(), null!, DateTime.MinValue, DateTime.MinValue);
            Assert.Empty(DuplicateChatRowMerge.OrderForMerge(null!, _ => false, _ => 0));
        }
    }
}
