// =============================================================================
// Tests for MessageTimestampValidity.
//
// This rule guards the timestamp every incoming message is sorted and previewed
// by. It used to read DateTime.UtcNow internally, which made the boundaries
// untestable, and it compared a raw timestamp against that clock -- so near the
// future cut-off the answer depended on the machine's time zone.
// =============================================================================
using System;
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class MessageTimestampValidityTests
    {
        private static readonly DateTime Now = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);

        // --- Rejections ---------------------------------------------------------

        [Fact]
        public void A_message_with_no_time_at_all_is_rejected()
        {
            Assert.False(MessageTimestampValidity.IsValid(DateTime.MinValue, Now));
        }

        [Fact]
        public void A_time_from_before_WhatsApp_existed_is_a_decoding_accident()
        {
            Assert.False(MessageTimestampValidity.IsValid(new DateTime(2008, 12, 31, 23, 59, 59, DateTimeKind.Utc), Now));
        }

        [Fact]
        public void A_time_far_in_the_future_is_rejected_before_it_pins_itself_to_the_top()
        {
            Assert.False(MessageTimestampValidity.IsValid(Now.AddYears(1), Now));
        }

        // --- Acceptances ---------------------------------------------------------

        [Fact]
        public void An_ordinary_recent_message_is_accepted()
        {
            Assert.True(MessageTimestampValidity.IsValid(Now.AddMinutes(-5), Now));
        }

        [Fact]
        public void A_genuinely_old_message_is_still_accepted()
        {
            // History sync legitimately carries messages from years back.
            Assert.True(MessageTimestampValidity.IsValid(new DateTime(2011, 5, 4, 8, 30, 0, DateTimeKind.Utc), Now));
        }

        [Fact]
        public void A_clock_slightly_ahead_of_ours_is_tolerated()
        {
            // Skew between phone, server and companion is real. Rejecting it would drop
            // legitimate messages.
            Assert.True(MessageTimestampValidity.IsValid(Now.AddHours(6), Now));
        }

        // --- The boundaries ------------------------------------------------------

        [Fact]
        public void The_first_instant_of_2009_is_inside_the_range()
        {
            Assert.True(MessageTimestampValidity.IsValid(new DateTime(2009, 1, 1, 0, 0, 0, DateTimeKind.Utc), Now));
        }

        [Fact]
        public void The_future_cut_off_includes_its_own_edge()
        {
            Assert.True(MessageTimestampValidity.IsValid(Now + MessageTimestampValidity.FutureTolerance, Now));
            Assert.False(MessageTimestampValidity.IsValid(
                Now + MessageTimestampValidity.FutureTolerance + TimeSpan.FromSeconds(1),
                Now));
        }

        // --- Kind handling, which is where this used to go wrong --------------------

        [Fact]
        public void A_timestamp_read_back_from_storage_is_judged_the_same_as_a_live_one()
        {
            // SQLite hands back DateTimeKind.Unspecified holding UTC wall-clock. Treating
            // it as local time would shift it by the machine's offset, and near the future
            // cut-off that shift decides acceptance -- so the same message would be valid
            // in one time zone and dropped in another.
            var live = new DateTime(2026, 9, 16, 11, 0, 0, DateTimeKind.Utc);
            var fromStorage = new DateTime(2026, 9, 16, 11, 0, 0, DateTimeKind.Unspecified);

            Assert.Equal(
                MessageTimestampValidity.IsValid(live, Now),
                MessageTimestampValidity.IsValid(fromStorage, Now));
        }

        [Fact]
        public void The_verdict_does_not_depend_on_how_the_current_time_was_expressed()
        {
            var timestamp = Now.AddHours(-1);

            Assert.Equal(
                MessageTimestampValidity.IsValid(timestamp, Now),
                MessageTimestampValidity.IsValid(timestamp, DateTime.SpecifyKind(Now, DateTimeKind.Unspecified)));
        }

        // --- Discarding ------------------------------------------------------------

        [Fact]
        public void A_believable_timestamp_is_handed_back_untouched()
        {
            // Deliberately not normalized on the way out: this is a filter, not a
            // converter, and quietly rewriting the value would hide where conversion
            // actually belongs.
            var timestamp = new DateTime(2026, 9, 16, 11, 0, 0, DateTimeKind.Unspecified);

            Assert.Equal(timestamp, MessageTimestampValidity.KeepOrDiscard(timestamp, Now));
            Assert.Equal(timestamp.Kind, MessageTimestampValidity.KeepOrDiscard(timestamp, Now).Kind);
        }

        [Fact]
        public void A_rejected_timestamp_becomes_no_time_rather_than_now()
        {
            // The whole point. Substituting the current time would promote a replayed old
            // event to the newest message in the conversation.
            Assert.Equal(DateTime.MinValue, MessageTimestampValidity.KeepOrDiscard(Now.AddYears(5), Now));
            Assert.Equal(DateTime.MinValue, MessageTimestampValidity.KeepOrDiscard(DateTime.MinValue, Now));
        }
    }
}
