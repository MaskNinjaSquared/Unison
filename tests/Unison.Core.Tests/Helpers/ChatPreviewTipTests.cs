// =============================================================================
// Tests for ChatPreviewTip.
//
// The last-message line in the chat list. Wrong here is wrong somewhere the
// user reads at a glance and would not think to report.
// =============================================================================
using System;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ChatPreviewTipTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);

        private static ChatMessage Msg(string? id, int seconds = 0, bool fromMe = false) =>
            new ChatMessage { Id = id, Timestamp = T0.AddSeconds(seconds), IsFromMe = fromMe };

        // --- PickNewer -------------------------------------------------------

        [Fact]
        public void With_only_one_source_that_source_wins()
        {
            var stored = Msg("db");
            var remembered = Msg("mem");

            Assert.Same(stored, ChatPreviewTip.PickNewer(stored, null));
            Assert.Same(remembered, ChatPreviewTip.PickNewer(null, remembered));
            Assert.Null(ChatPreviewTip.PickNewer(null, null));
        }

        [Fact]
        public void The_newer_timestamp_wins_whichever_side_it_is_on()
        {
            var stored = Msg("db", seconds: 10);
            var remembered = Msg("mem", seconds: 20);
            var newerStored = Msg("db", seconds: 30);

            Assert.Same(remembered, ChatPreviewTip.PickNewer(stored, remembered));
            Assert.Same(newerStored, ChatPreviewTip.PickNewer(newerStored, remembered));
        }

        [Fact]
        public void A_live_message_the_store_has_not_caught_up_with_still_wins()
        {
            // The case the memory source exists for: an open chat showing a message
            // that has not been flushed yet.
            var stored = Msg("db", seconds: 0);
            var remembered = Msg("mem", seconds: 1);

            Assert.Same(remembered, ChatPreviewTip.PickNewer(stored, remembered));
        }

        [Fact]
        public void On_the_same_second_a_message_of_ours_breaks_the_tie()
        {
            // A cross-device echo arrives with the same wall clock as the stored row.
            var stored = Msg("db", seconds: 5, fromMe: false);
            var remembered = Msg("mem", seconds: 5, fromMe: true);

            Assert.Same(remembered, ChatPreviewTip.PickNewer(stored, remembered));
        }

        [Fact]
        public void Being_ours_never_beats_a_strictly_newer_message()
        {
            var stored = Msg("db", seconds: 9, fromMe: false);
            var remembered = Msg("mem", seconds: 5, fromMe: true);

            Assert.Same(stored, ChatPreviewTip.PickNewer(stored, remembered));
        }

        [Fact]
        public void An_exact_tie_falls_back_to_the_id_so_the_answer_is_stable()
        {
            var stored = Msg("aaa", seconds: 5);
            var remembered = Msg("bbb", seconds: 5);

            Assert.Same(remembered, ChatPreviewTip.PickNewer(stored, remembered));
            var lowerId = Msg("aa", 5);
            Assert.Same(stored, ChatPreviewTip.PickNewer(stored, lowerId));
        }

        [Fact]
        public void A_timestamp_with_no_kind_is_read_as_utc()
        {
            // Guards the same +3h strip shift as elsewhere: an Unspecified row from
            // SQLite must not appear three hours newer than it is.
            var stored = new ChatMessage
            {
                Id = "db",
                Timestamp = new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Unspecified)
            };
            var remembered = Msg("mem", seconds: 1);

            Assert.Same(remembered, ChatPreviewTip.PickNewer(stored, remembered));
        }

        // --- IsAlreadyShowing ------------------------------------------------

        private static ChatItem Row(string? lastId, string lastMessage, bool fromMe = false) =>
            new ChatItem { LastMessageId = lastId, LastMessage = lastMessage, LastMessageIsFromMe = fromMe };

        [Fact]
        public void A_row_showing_the_same_tip_is_left_alone()
        {
            var chat = Row("m1", "Oi");

            Assert.True(ChatPreviewTip.IsAlreadyShowing(chat, Msg("m1"), "Oi"));
        }

        [Fact]
        public void A_different_message_means_the_row_is_stale()
        {
            var chat = Row("m1", "Oi");

            Assert.False(ChatPreviewTip.IsAlreadyShowing(chat, Msg("m2"), "Oi"));
        }

        [Fact]
        public void The_same_message_rendered_differently_still_needs_writing()
        {
            // An edit, or a preview that only became renderable once media arrived.
            var chat = Row("m1", "Foto");

            Assert.False(ChatPreviewTip.IsAlreadyShowing(chat, Msg("m1"), "Foto: praia"));
        }

        [Fact]
        public void A_change_of_direction_needs_writing()
        {
            // The check-mark prefix on the row depends on this.
            var chat = Row("m1", "Oi", fromMe: false);

            Assert.False(ChatPreviewTip.IsAlreadyShowing(chat, Msg("m1", fromMe: true), "Oi"));
        }

        [Fact]
        public void A_tip_without_an_id_can_never_be_confirmed_as_already_showing()
        {
            var chat = Row(null, "Oi");

            Assert.False(ChatPreviewTip.IsAlreadyShowing(chat, Msg(null), "Oi"));
        }

        [Fact]
        public void Missing_arguments_are_not_a_match()
        {
            Assert.False(ChatPreviewTip.IsAlreadyShowing(null, Msg("m1"), "Oi"));
            Assert.False(ChatPreviewTip.IsAlreadyShowing(Row("m1", "Oi"), null, "Oi"));
        }

        // --- ShouldStampMissingMessageId -------------------------------------

        private static ChatItem LegacyRow(string? lastId, DateTime? stripUtc) =>
            new ChatItem { LastMessageId = lastId, LastMessageTimestampUtc = stripUtc };

        [Fact]
        public void A_row_from_the_old_schema_gets_its_id_stamped()
        {
            var chat = LegacyRow(null, T0);

            Assert.True(ChatPreviewTip.ShouldStampMissingMessageId(chat, Msg("m1", seconds: 0)));
        }

        [Fact]
        public void A_row_that_already_has_an_id_is_left_alone()
        {
            var chat = LegacyRow("existing", T0);

            Assert.False(ChatPreviewTip.ShouldStampMissingMessageId(chat, Msg("m1")));
        }

        [Fact]
        public void There_is_nothing_to_stamp_from_a_tip_without_an_id()
        {
            var chat = LegacyRow(null, T0);

            Assert.False(ChatPreviewTip.ShouldStampMissingMessageId(chat, Msg(null)));
        }

        [Fact]
        public void A_stale_tip_does_not_claim_a_strip_that_moved_on()
        {
            var chat = LegacyRow(null, T0.AddMinutes(5));

            Assert.False(ChatPreviewTip.ShouldStampMissingMessageId(chat, Msg("m1", seconds: 0)));
        }

        [Fact]
        public void A_row_that_never_had_a_timestamp_accepts_the_stamp()
        {
            var chat = LegacyRow(null, null);

            Assert.True(ChatPreviewTip.ShouldStampMissingMessageId(chat, Msg("m1")));
        }

        [Fact]
        public void A_tip_with_no_timestamp_at_all_stamps_nothing()
        {
            var chat = LegacyRow(null, null);
            var undated = new ChatMessage { Id = "m1", Timestamp = DateTime.MinValue };

            Assert.False(ChatPreviewTip.ShouldStampMissingMessageId(chat, undated));
        }
    }
}
