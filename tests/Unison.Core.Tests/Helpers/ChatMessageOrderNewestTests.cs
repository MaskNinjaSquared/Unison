// =============================================================================
// Tests for ChatMessageOrder.NewestComparableUtc.
//
// This exists because of a defect that has now been found four times in this
// codebase, always in the same shape: comparing ChatMessage.Timestamp values
// directly.
//
// A chat's message list holds live messages, stamped DateTimeKind.Utc, next to
// rows read back from SQLite, stamped DateTimeKind.Unspecified. Unspecified is
// already UTC wall-clock here, but the runtime does not know that, so a raw
// comparison between the two is off by the local offset -- three hours in
// Brazil. The visible result is the chat list showing the wrong last message.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ChatMessageOrderNewestTests
    {
        private const int BrazilOffsetHours = 3;

        private static readonly DateTime Noon = new DateTime(2026, 2, 10, 12, 0, 0, DateTimeKind.Utc);

        private static ChatMessage Live(int minutesAfterNoon) =>
            new ChatMessage { Id = "live", Timestamp = Noon.AddMinutes(minutesAfterNoon) };

        /// <summary>A row as it comes back from SQLite: right wall-clock, no kind.</summary>
        private static ChatMessage FromStore(int minutesAfterNoon) =>
            new ChatMessage
            {
                Id = "store",
                Timestamp = DateTime.SpecifyKind(Noon.AddMinutes(minutesAfterNoon), DateTimeKind.Unspecified)
            };

        [Fact]
        public void An_empty_or_missing_set_has_no_newest()
        {
            Assert.Equal(DateTime.MinValue, ChatMessageOrder.NewestComparableUtc(null));
            Assert.Equal(DateTime.MinValue, ChatMessageOrder.NewestComparableUtc(new List<ChatMessage>()));
        }

        [Fact]
        public void Gaps_in_the_set_are_skipped()
        {
            var messages = new List<ChatMessage> { null!, Live(5), null! };

            Assert.Equal(Noon.AddMinutes(5), ChatMessageOrder.NewestComparableUtc(messages));
        }

        [Fact]
        public void The_newest_of_several_live_messages_is_returned()
        {
            var messages = new List<ChatMessage> { Live(0), Live(30), Live(10) };

            Assert.Equal(Noon.AddMinutes(30), ChatMessageOrder.NewestComparableUtc(messages));
        }

        [Fact]
        public void A_stored_row_is_read_at_its_face_value_not_shifted()
        {
            // The whole point: Unspecified must not be put through ToUniversalTime.
            var messages = new List<ChatMessage> { FromStore(0) };

            Assert.Equal(Noon, ChatMessageOrder.NewestComparableUtc(messages));
        }

        [Fact]
        public void A_stored_row_does_not_outrank_a_genuinely_newer_live_one()
        {
            // The defect, stated directly. Read raw, the stored row would appear to be at
            // 15:00 rather than 12:00 and would win against a live message from 12:05.
            var storedEarlier = FromStore(0);
            var liveLater = Live(5);

            DateTime newest = ChatMessageOrder.NewestComparableUtc(
                new List<ChatMessage> { storedEarlier, liveLater });

            Assert.Equal(Noon.AddMinutes(5), newest);
            Assert.NotEqual(Noon.AddHours(BrazilOffsetHours), newest);
        }

        [Fact]
        public void A_stored_row_still_wins_when_it_really_is_newer()
        {
            // The fix must not overshoot into always preferring live messages.
            var messages = new List<ChatMessage> { Live(0), FromStore(20) };

            Assert.Equal(Noon.AddMinutes(20), ChatMessageOrder.NewestComparableUtc(messages));
        }

        [Fact]
        public void The_order_of_the_set_does_not_change_the_answer()
        {
            var ascending = new List<ChatMessage> { FromStore(0), Live(5), FromStore(20) };
            var descending = new List<ChatMessage> { FromStore(20), Live(5), FromStore(0) };

            Assert.Equal(
                ChatMessageOrder.NewestComparableUtc(ascending),
                ChatMessageOrder.NewestComparableUtc(descending));
        }

        [Fact]
        public void A_message_with_no_timestamp_does_not_become_the_answer()
        {
            var undated = new ChatMessage { Id = "undated", Timestamp = DateTime.MinValue };

            Assert.Equal(Noon, ChatMessageOrder.NewestComparableUtc(new List<ChatMessage> { undated, Live(0) }));
        }

        [Fact]
        public void A_set_of_nothing_but_undated_messages_reports_no_newest()
        {
            var undated = new ChatMessage { Id = "undated", Timestamp = DateTime.MinValue };

            Assert.Equal(DateTime.MinValue, ChatMessageOrder.NewestComparableUtc(new List<ChatMessage> { undated }));
        }

        [Fact]
        public void The_answer_agrees_with_the_one_PickLatest_gives()
        {
            // These two are used side by side -- one for the message, one for the instant.
            // They must not disagree about which message is newest.
            var messages = new List<ChatMessage> { FromStore(0), Live(5), FromStore(20) };

            ChatMessage latest = ChatPreviewTip.PickLatest(messages);

            Assert.Equal(
                ChatMessageOrder.NewestComparableUtc(messages),
                ChatMessageOrder.ToComparableUtc(latest.Timestamp));
        }
    }
}
