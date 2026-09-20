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

        // --- PickLatest ------------------------------------------------------

        [Fact]
        public void The_newest_remaining_message_becomes_the_preview()
        {
            var messages = new[] { Msg("a", 0), Msg("c", 30), Msg("b", 10) };

            Assert.Equal("c", ChatPreviewTip.PickLatest(messages)?.Id);
        }

        [Fact]
        public void A_chat_emptied_of_messages_has_no_latest()
        {
            Assert.Null(ChatPreviewTip.PickLatest(new ChatMessage[0]));
            Assert.Null(ChatPreviewTip.PickLatest(null));
        }

        [Fact]
        public void Gaps_in_the_list_are_skipped_rather_than_chosen()
        {
            var messages = new[] { null, Msg("a", 5), null };

            Assert.Equal("a", ChatPreviewTip.PickLatest(messages)?.Id);
        }

        [Fact]
        public void A_kind_less_timestamp_does_not_jump_the_queue()
        {
            // Rows read back from SQLite arrive Unspecified. Ordering on the raw DateTime
            // let one of them appear a local offset newer than it is, which promoted the
            // wrong message to the preview after a delete.
            var fromStore = new ChatMessage
            {
                Id = "store",
                Timestamp = new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Unspecified)
            };
            var live = Msg("live", seconds: 1);

            Assert.Equal("live", ChatPreviewTip.PickLatest(new[] { fromStore, live })?.Id);
        }

        [Fact]
        public void The_only_message_left_wins_even_without_a_timestamp()
        {
            var undated = new ChatMessage { Id = "only", Timestamp = DateTime.MinValue };

            Assert.Equal("only", ChatPreviewTip.PickLatest(new[] { undated })?.Id);
        }

        // --- Clear ------------------------------------------------------------

        [Fact]
        public void Clearing_blanks_everything_the_row_was_showing()
        {
            var chat = new ChatItem
            {
                LastMessage = "Oi",
                LastMessageAuthor = "Ana",
                LastMessageMentionedJids = new System.Collections.Generic.List<string> { "x@s.whatsapp.net" },
                LastMessageKind = ChatPreviewKind.Image,
                LastMessageId = "m1",
                Timestamp = "12:00",
                LastMessageTimestampUtc = T0
            };

            ChatPreviewTip.Clear(chat);

            Assert.Equal(string.Empty, chat.LastMessage);
            Assert.Equal(string.Empty, chat.LastMessageAuthor);
            Assert.Null(chat.LastMessageMentionedJids);
            Assert.Equal(ChatPreviewKind.Text, chat.LastMessageKind);
            Assert.Null(chat.LastMessageId);
            Assert.Equal(string.Empty, chat.Timestamp);
            Assert.Null(chat.LastMessageTimestampUtc);
        }

        [Fact]
        public void Clearing_a_missing_row_is_not_an_error()
        {
            ChatPreviewTip.Clear(null);
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

        // --- ShowsOutgoingMessage --------------------------------------------
        //
        // Which message the strip is showing, and therefore whose delivery state it
        // should follow. Getting it wrong pulls a strip backwards from read to sent.

        private static readonly TimeSpan Window = TimeSpan.FromSeconds(2);

        private static ChatItem SentRow(string? lastId, int? seconds = 0) => new ChatItem
        {
            LastMessageIsFromMe = true,
            LastMessageId = lastId,
            LastMessageTimestampUtc = seconds.HasValue ? T0.AddSeconds(seconds.Value) : (DateTime?)null
        };

        [Fact]
        public void The_id_decides_whenever_the_row_has_one()
        {
            var row = SentRow("m2", seconds: 10);

            Assert.True(ChatPreviewTip.ShowsOutgoingMessage(row, Msg("m2", 10, fromMe: true), Window));
            Assert.False(ChatPreviewTip.ShowsOutgoingMessage(row, Msg("m1", 10, fromMe: true), Window));
        }

        [Fact]
        public void Two_messages_sent_a_second_apart_are_told_apart_by_id_not_by_clock()
        {
            // The regression this rule exists for. Sending twice in quick succession is
            // ordinary, and a clock-only match let the older message's receipt land on the
            // newer message's strip -- the visible symptom being a double tick dropping
            // back to a single one on its own.
            var row = SentRow("m2", seconds: 11);
            var older = Msg("m1", seconds: 10, fromMe: true);

            Assert.False(ChatPreviewTip.ShowsOutgoingMessage(row, older, Window));
        }

        [Fact]
        public void A_row_from_before_ids_were_stored_still_falls_back_to_the_clock()
        {
            // Rows written by an older schema have no id to match on, and refusing them
            // outright would freeze their tick forever.
            var legacy = SentRow(null, seconds: 10);

            Assert.True(ChatPreviewTip.ShowsOutgoingMessage(legacy, Msg("m1", 11, fromMe: true), Window));
            Assert.False(ChatPreviewTip.ShowsOutgoingMessage(legacy, Msg("m1", 30, fromMe: true), Window));
        }

        [Fact]
        public void The_clock_fallback_measures_distance_in_both_directions()
        {
            var legacy = SentRow(null, seconds: 10);

            Assert.True(ChatPreviewTip.ShowsOutgoingMessage(legacy, Msg(null, 8, fromMe: true), Window));
            Assert.False(ChatPreviewTip.ShowsOutgoingMessage(legacy, Msg(null, 5, fromMe: true), Window));
        }

        [Fact]
        public void With_no_clock_on_either_side_the_strip_is_assumed_to_be_this_message()
        {
            // Nothing left to disagree with, and the alternative is a row whose tick can
            // never move again.
            var undated = SentRow(null, seconds: null);

            Assert.True(ChatPreviewTip.ShowsOutgoingMessage(undated, Msg(null, 0, fromMe: true), Window));
        }

        [Fact]
        public void A_strip_showing_someone_elses_message_never_follows_our_receipt()
        {
            var theirs = new ChatItem { LastMessageIsFromMe = false, LastMessageId = "m1" };

            Assert.False(ChatPreviewTip.ShowsOutgoingMessage(theirs, Msg("m1", 0, fromMe: true), Window));
        }

        [Fact]
        public void An_incoming_message_is_not_ours_to_report_on()
        {
            var row = SentRow("m1", seconds: 0);

            Assert.False(ChatPreviewTip.ShowsOutgoingMessage(row, Msg("m1", 0, fromMe: false), Window));
        }

        [Fact]
        public void Nothing_to_compare_is_not_a_match()
        {
            Assert.False(ChatPreviewTip.ShowsOutgoingMessage(null, Msg("m1", 0, fromMe: true), Window));
            Assert.False(ChatPreviewTip.ShowsOutgoingMessage(SentRow("m1"), null, Window));
        }
    }
}
