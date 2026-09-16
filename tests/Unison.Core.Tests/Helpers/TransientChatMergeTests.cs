// =============================================================================
// Tests for TransientChatMerge.
//
// Two rows for one contact collapse into one. Whatever loses here is gone: the
// surviving row is what the user reads and what gets written back, and nothing
// reports that a field was dropped or taken from the wrong side.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class TransientChatMergeTests
    {
        private const string PhoneJid = "5511999990000@s.whatsapp.net";
        private const string LidJid = "204837261509384@lid";

        private static readonly DateTime T0 = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// Stands in for the localized "(You)" marker without loading UWP resources.
        /// </summary>
        private sealed class FakeSelfMarker : ISelfMarkerNaming
        {
            public bool IsMarkerLabel(string label) =>
                string.Equals(label?.Trim(), "(You)", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(label?.Trim(), "You", StringComparison.OrdinalIgnoreCase);

            public string StripMarker(string label)
            {
                // null!: Unison.Core is not annotated, and null is the meaningful answer
                // when nothing is left once the marker comes off.
                string? trimmed = label?.Trim();
                if (string.IsNullOrEmpty(trimmed))
                {
                    return null!;
                }

                return IsMarkerLabel(trimmed) ? null! : trimmed!;
            }
        }

        private static void Merge(ChatItem canonical, ChatItem transient) =>
            TransientChatMerge.Apply(canonical, transient, PhoneJid, LidJid, new FakeSelfMarker());

        private static ChatItem Preview(string? text, DateTime? at, string clock = "12:00") =>
            new ChatItem { LastMessage = text, Timestamp = clock, LastMessageTimestampUtc = at };

        // --- Which preview the row keeps ---------------------------------------

        [Fact]
        public void The_newer_of_the_two_previews_is_the_one_the_row_keeps()
        {
            // The transient row is usually the one the user has been reading, so its
            // last message is the one they expect to still see after the merge.
            var canonical = Preview("Oi", T0);
            var transient = Preview("Chegando", T0.AddMinutes(5), "12:05");

            Merge(canonical, transient);

            Assert.Equal("Chegando", canonical.LastMessage);
            Assert.Equal(T0.AddMinutes(5), canonical.LastMessageTimestampUtc);
        }

        [Fact]
        public void An_older_transient_preview_does_not_drag_the_row_backwards()
        {
            // Merging would otherwise look like the conversation went quiet, and the
            // chat list would reorder the row down past chats it should outrank.
            var canonical = Preview("Chegando", T0.AddMinutes(5), "12:05");
            var transient = Preview("Oi", T0);

            Merge(canonical, transient);

            Assert.Equal("Chegando", canonical.LastMessage);
            Assert.Equal("12:05", canonical.Timestamp);
            Assert.Equal(T0.AddMinutes(5), canonical.LastMessageTimestampUtc);
        }

        [Fact]
        public void A_row_with_nothing_to_show_takes_the_older_preview_anyway()
        {
            // A blank strip is worse than a stale one: the contact looks like a chat
            // that never happened.
            var canonical = new ChatItem { LastMessageTimestampUtc = T0.AddMinutes(5) };
            var transient = Preview("Oi", T0);

            Merge(canonical, transient);

            Assert.Equal("Oi", canonical.LastMessage);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void A_transient_row_with_no_message_never_blanks_the_one_that_has_it(string? text)
        {
            // The transient row can exist purely as an address with no history behind it.
            var canonical = Preview("Oi", T0);
            var transient = Preview(text, T0.AddMinutes(5), "12:05");

            Merge(canonical, transient);

            Assert.Equal("Oi", canonical.LastMessage);
            Assert.Equal("12:00", canonical.Timestamp);
            Assert.Equal(T0, canonical.LastMessageTimestampUtc);
        }

        [Fact]
        public void A_preview_arrives_whole_or_not_at_all()
        {
            // Half a migration — new text under an old author, or a new body with the
            // old kind chip — renders a message that was never sent.
            var canonical = new ChatItem
            {
                LastMessage = "Oi",
                LastMessageKind = ChatPreviewKind.Text,
                LastMessageAuthor = "Bruno",
                LastMessageIsFromMe = false,
                LastMessageSendState = MessageSendState.NotApplicable,
                Timestamp = "12:00",
                LastMessageTimestampUtc = T0
            };
            var transient = new ChatItem
            {
                LastMessage = "Foto",
                LastMessageKind = ChatPreviewKind.Image,
                LastMessageAuthor = "Ana",
                LastMessageIsFromMe = true,
                LastMessageSendState = MessageSendState.Read,
                LastMessageMentionedJids = new List<string> { "5511911112222@s.whatsapp.net" },
                Timestamp = "12:05",
                LastMessageTimestampUtc = T0.AddMinutes(5)
            };

            Merge(canonical, transient);

            Assert.Equal("Foto", canonical.LastMessage);
            Assert.Equal(ChatPreviewKind.Image, canonical.LastMessageKind);
            Assert.Equal("Ana", canonical.LastMessageAuthor);
            Assert.True(canonical.LastMessageIsFromMe);
            Assert.Equal(MessageSendState.Read, canonical.LastMessageSendState);
            Assert.Equal(
                new[] { "5511911112222@s.whatsapp.net" },
                canonical.LastMessageMentionedJids);
            Assert.Equal("12:05", canonical.Timestamp);
            Assert.Equal(T0.AddMinutes(5), canonical.LastMessageTimestampUtc);
        }

        [Fact]
        public void A_refused_preview_leaves_every_field_of_the_old_one_in_place()
        {
            // The same all-or-nothing rule read from the losing side.
            var canonical = new ChatItem
            {
                LastMessage = "Foto",
                LastMessageKind = ChatPreviewKind.Image,
                LastMessageAuthor = "Ana",
                Timestamp = "12:05",
                LastMessageTimestampUtc = T0.AddMinutes(5)
            };
            var transient = new ChatItem
            {
                LastMessage = "Oi",
                LastMessageKind = ChatPreviewKind.Text,
                LastMessageAuthor = "Bruno",
                Timestamp = "12:00",
                LastMessageTimestampUtc = T0
            };

            Merge(canonical, transient);

            Assert.Equal("Foto", canonical.LastMessage);
            Assert.Equal(ChatPreviewKind.Image, canonical.LastMessageKind);
            Assert.Equal("Ana", canonical.LastMessageAuthor);
        }

        // --- Which message the row claims to be showing --------------------------

        private static ChatItem Strip(string text, string? id, DateTime at, string clock) =>
            new ChatItem
            {
                LastMessage = text,
                LastMessageId = id,
                LastMessageTimestampUtc = at,
                Timestamp = clock
            };

        [Fact]
        public void The_id_of_the_visible_message_travels_with_its_text()
        {
            // The id names which message the row is showing. Left behind, it points at a
            // message the user can no longer see on that row.
            var canonical = Strip("Oi", "m1", T0, "12:00");
            var transient = Strip("Chegando", "m2", T0.AddMinutes(5), "12:05");

            Merge(canonical, transient);

            Assert.Equal("Chegando", canonical.LastMessage);
            Assert.Equal("m2", canonical.LastMessageId);
        }

        [Fact]
        public void A_refused_preview_keeps_the_id_of_the_text_that_stayed()
        {
            // The same rule from the losing side: an id must never arrive without the
            // text it belongs to either.
            var canonical = Strip("Chegando", "m2", T0.AddMinutes(5), "12:05");
            var transient = Strip("Oi", "m1", T0, "12:00");

            Merge(canonical, transient);

            Assert.Equal("Chegando", canonical.LastMessage);
            Assert.Equal("m2", canonical.LastMessageId);
        }

        [Fact]
        public void The_merged_row_agrees_with_itself_about_which_message_it_shows()
        {
            // Read through the consumer that compares both: a row whose id and text name
            // different messages reports itself as stale on every pass, so the list
            // repaints forever and the reconcile never settles.
            var canonical = Strip("Oi", "m1", T0, "12:00");
            var transient = Strip("Chegando", "m2", T0.AddMinutes(5), "12:05");
            var tip = new ChatMessage { Id = "m2", Timestamp = T0.AddMinutes(5), IsFromMe = false };

            Merge(canonical, transient);

            Assert.True(ChatPreviewTip.IsAlreadyShowing(canonical, tip, "Chegando"));
        }

        [Fact]
        public void A_winning_preview_without_an_id_clears_the_old_one_instead_of_keeping_it()
        {
            // This is what made the mismatch permanent: the repair path only fills an id
            // in when it is missing, never when it is wrong. An empty id still heals; an
            // id belonging to the previous message never does.
            var canonical = Strip("Oi", "m1", T0, "12:00");
            var transient = Strip("Chegando", null, T0.AddMinutes(5), "12:05");
            var tip = new ChatMessage { Id = "m2", Timestamp = T0.AddMinutes(5) };

            Merge(canonical, transient);

            Assert.Null(canonical.LastMessageId);
            Assert.True(ChatPreviewTip.ShouldStampMissingMessageId(canonical, tip));
        }

        // --- Timestamps that came back from SQLite -------------------------------

        [Fact]
        public void A_stored_preview_is_not_treated_as_hours_newer_than_it_is()
        {
            // SQLite hands back Unspecified, which is already UTC wall clock. Reading it
            // as local time shifts it by the device offset, and a genuinely newer live
            // preview then loses to a stored one that only looks newer.
            var canonical = new ChatItem
            {
                LastMessage = "Oi",
                LastMessageTimestampUtc = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Unspecified)
            };
            var transient = Preview("Chegando", T0.AddSeconds(1), "12:00");

            Merge(canonical, transient);

            Assert.Equal("Chegando", canonical.LastMessage);
        }

        [Fact]
        public void A_stored_preview_is_not_treated_as_hours_older_than_it_is()
        {
            // The same shift in the other direction, which discards the newer strip.
            var canonical = Preview("Chegando", T0.AddSeconds(1));
            var transient = new ChatItem
            {
                LastMessage = "Oi",
                LastMessageTimestampUtc = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Unspecified)
            };

            Merge(canonical, transient);

            Assert.Equal("Chegando", canonical.LastMessage);
        }

        [Fact]
        public void A_preview_with_no_timestamp_ranks_below_one_that_has_it()
        {
            var canonical = Preview("Oi", T0);
            var transient = Preview("Chegando", null);

            Merge(canonical, transient);

            Assert.Equal("Oi", canonical.LastMessage);
        }

        [Fact]
        public void Two_undated_previews_leave_the_canonical_one_standing()
        {
            // Neither is newer, so there is no reason to prefer the weaker identity.
            var canonical = Preview("Oi", null);
            var transient = Preview("Chegando", null);

            Merge(canonical, transient);

            Assert.Equal("Oi", canonical.LastMessage);
        }

        // --- Unread ---------------------------------------------------------------

        [Fact]
        public void The_higher_unread_count_wins_rather_than_the_sum()
        {
            // Both rows were counting the same conversation. Adding them double-counts
            // every message that arrived while the alias was still unknown.
            var canonical = new ChatItem { UnreadCount = 3 };
            var transient = new ChatItem { UnreadCount = 5 };

            Merge(canonical, transient);

            Assert.Equal(5, canonical.UnreadCount);
        }

        [Fact]
        public void A_lower_transient_count_does_not_mark_messages_as_read()
        {
            var canonical = new ChatItem { UnreadCount = 7 };
            var transient = new ChatItem { UnreadCount = 2 };

            Merge(canonical, transient);

            Assert.Equal(7, canonical.UnreadCount);
        }

        [Fact]
        public void A_transient_row_nobody_opened_still_contributes_its_badge()
        {
            var canonical = new ChatItem { UnreadCount = 0 };
            var transient = new ChatItem { UnreadCount = 4 };

            Merge(canonical, transient);

            Assert.Equal(4, canonical.UnreadCount);
            Assert.True(canonical.HasUnread);
        }

        // --- Avatar ----------------------------------------------------------------

        [Fact]
        public void A_photo_is_only_borrowed_by_a_row_that_has_none()
        {
            var canonical = new ChatItem();
            var transient = new ChatItem { AvatarUrl = "ms-appdata:///local/ana.jpg" };

            Merge(canonical, transient);

            Assert.Equal("ms-appdata:///local/ana.jpg", canonical.AvatarUrl);
        }

        [Fact]
        public void A_row_that_already_has_a_photo_keeps_it_even_if_the_other_is_newer()
        {
            // Unlike the preview, a newer avatar is not a better one: the canonical
            // picture was fetched against the identity being kept.
            var canonical = new ChatItem
            {
                AvatarUrl = "ms-appdata:///local/ana.jpg",
                AvatarFetchedAtUtc = T0
            };
            var transient = new ChatItem
            {
                AvatarUrl = "ms-appdata:///local/lid.jpg",
                AvatarFetchedAtUtc = T0.AddHours(2)
            };

            Merge(canonical, transient);

            Assert.Equal("ms-appdata:///local/ana.jpg", canonical.AvatarUrl);
            Assert.Equal(T0, canonical.AvatarFetchedAtUtc);
        }

        [Fact]
        public void A_borrowed_photo_brings_its_own_fetch_history_with_it()
        {
            // Left behind, the canonical row's retry stamps describe a fetch against a
            // url it no longer holds, and the avatar loop either retries forever or
            // never retries at all.
            var canonical = new ChatItem
            {
                AvatarFetchedAtUtc = T0.AddDays(-3),
                AvatarFetchFailedAtUtc = T0.AddDays(-3),
                AvatarFetchFailureReason = "stale-canonical-failure"
            };
            var transient = new ChatItem
            {
                AvatarUrl = "ms-appdata:///local/ana.jpg",
                AvatarFetchedAtUtc = T0,
                AvatarFetchFailedAtUtc = null,
                AvatarFetchFailureReason = null
            };

            Merge(canonical, transient);

            Assert.Equal("ms-appdata:///local/ana.jpg", canonical.AvatarUrl);
            Assert.Equal(T0, canonical.AvatarFetchedAtUtc);
            Assert.Null(canonical.AvatarFetchFailedAtUtc);
            Assert.Null(canonical.AvatarFetchFailureReason);
        }

        [Fact]
        public void A_transient_row_with_no_photo_does_not_erase_the_fetch_history()
        {
            // Nothing to copy, so the canonical row's record of its own attempts stands.
            var canonical = new ChatItem
            {
                AvatarFetchFailedAtUtc = T0,
                AvatarFetchFailureReason = "rate-limited"
            };
            var transient = new ChatItem { AvatarUrl = "   " };

            Merge(canonical, transient);

            Assert.Null(canonical.AvatarUrl);
            Assert.Equal(T0, canonical.AvatarFetchFailedAtUtc);
            Assert.Equal("rate-limited", canonical.AvatarFetchFailureReason);
        }

        // --- Avatar: the two resolutions are one fact ---------------------------------

        [Fact]
        public void A_row_with_no_picture_at_all_takes_both_resolutions()
        {
            // They are two files of one photo. Borrowing the list-sized one and leaving
            // the full-size one behind loses it for good, because the fetch stamp copied
            // alongside says the picture is current and nothing goes looking again.
            var canonical = new ChatItem();
            var transient = new ChatItem
            {
                AvatarUrl = "ms-appdata:///local/ana.jpg",
                AvatarHighUrl = "ms-appdata:///local/ana_high.jpg",
                AvatarFetchedAtUtc = T0
            };

            Merge(canonical, transient);

            Assert.Equal("ms-appdata:///local/ana.jpg", canonical.AvatarUrl);
            Assert.Equal("ms-appdata:///local/ana_high.jpg", canonical.AvatarHighUrl);
            Assert.Equal(T0, canonical.AvatarFetchedAtUtc);
        }

        [Fact]
        public void A_row_holding_only_the_full_size_picture_does_not_borrow_a_preview()
        {
            // Each resolution falls back to the other for display, so a row carrying the
            // preview of one identity and the full-size picture of another shows a
            // different face in the list than in the info pane.
            var canonical = new ChatItem
            {
                AvatarHighUrl = "ms-appdata:///local/ana_high.jpg",
                AvatarFetchedAtUtc = T0
            };
            var transient = new ChatItem
            {
                AvatarUrl = "ms-appdata:///local/lid.jpg",
                AvatarFetchedAtUtc = T0.AddHours(2)
            };

            Merge(canonical, transient);

            Assert.Null(canonical.AvatarUrl);
            Assert.Equal("ms-appdata:///local/ana_high.jpg", canonical.AvatarHighUrl);
            Assert.Equal(T0, canonical.AvatarFetchedAtUtc);
        }

        [Fact]
        public void A_row_holding_only_the_preview_does_not_borrow_a_full_size_picture()
        {
            // The same mismatch approached from the other resolution.
            var canonical = new ChatItem { AvatarUrl = "ms-appdata:///local/ana.jpg" };
            var transient = new ChatItem { AvatarHighUrl = "ms-appdata:///local/lid_high.jpg" };

            Merge(canonical, transient);

            Assert.Equal("ms-appdata:///local/ana.jpg", canonical.AvatarUrl);
            Assert.Null(canonical.AvatarHighUrl);
        }

        [Fact]
        public void A_transient_row_with_only_the_full_size_picture_is_still_worth_borrowing()
        {
            // A face is a face. Refusing it because the list-sized file is missing leaves
            // an empty circle next to a row that had a picture available all along.
            var canonical = new ChatItem();
            var transient = new ChatItem
            {
                AvatarHighUrl = "ms-appdata:///local/ana_high.jpg",
                AvatarFetchedAtUtc = T0
            };

            Merge(canonical, transient);

            Assert.Equal("ms-appdata:///local/ana_high.jpg", canonical.AvatarHighUrl);
            Assert.Equal(T0, canonical.AvatarFetchedAtUtc);
            Assert.Equal("ms-appdata:///local/ana_high.jpg", canonical.GetAvatarUrl(false));
        }

        [Theory]
        [InlineData("   ", null)]
        [InlineData(null, "   ")]
        public void A_blank_url_is_not_a_picture_on_either_resolution(string? previewUrl, string? highUrl)
        {
            // Rows come back from the store with empty strings where a url never was.
            // Reading those as "already has a photo" refuses a real one.
            var canonical = new ChatItem { AvatarUrl = previewUrl, AvatarHighUrl = highUrl };
            var transient = new ChatItem { AvatarUrl = "ms-appdata:///local/ana.jpg" };

            Merge(canonical, transient);

            Assert.Equal("ms-appdata:///local/ana.jpg", canonical.AvatarUrl);
        }

        // --- Name -------------------------------------------------------------------

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("5511999990000")]
        [InlineData("(You)")]
        public void A_row_that_is_not_really_named_accepts_the_name_the_other_one_learned(string? name)
        {
            // Blank, its own number echoed back, and a self-marker all read to the user
            // as "we do not know who this is" — the name is the whole point of the merge.
            var canonical = new ChatItem { Name = name };
            var transient = new ChatItem { Name = "Ana Paula" };

            Merge(canonical, transient);

            Assert.Equal("Ana Paula", canonical.Name);
        }

        [Fact]
        public void A_contact_who_calls_themselves_You_cannot_keep_that_label()
        {
            // A contact can set "(You)" as their push name. Left on their row, their
            // messages read as the user's own.
            var canonical = new ChatItem { Name = "(You)" };
            var transient = new ChatItem { Name = "Ana Paula" };

            Merge(canonical, transient);

            Assert.Equal("Ana Paula", canonical.Name);
        }

        [Fact]
        public void A_real_name_is_never_replaced_by_the_weaker_rows_guess()
        {
            // The canonical row's name came from the address book or a usync; the
            // transient one is whatever a push name claimed.
            var canonical = new ChatItem { Name = "Ana Paula" };
            var transient = new ChatItem { Name = "aninha ❤" };

            Merge(canonical, transient);

            Assert.Equal("Ana Paula", canonical.Name);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("204837261509384")]
        [InlineData("You")]
        public void A_nameless_transient_row_has_nothing_to_offer(string? name)
        {
            // Copying it would replace one non-name with another, and in the marker
            // case would hand the contact the label meant for the user's own chat.
            var canonical = new ChatItem { Name = "5511999990000" };
            var transient = new ChatItem { Name = name };

            Merge(canonical, transient);

            Assert.Equal("5511999990000", canonical.Name);
        }

        [Theory]
        [InlineData("5511999990000")]
        [InlineData("204837261509384")]
        public void Neither_rows_number_passes_as_the_name_of_the_merged_row(string offered)
        {
            // A LID row is routinely labelled with the very number the surviving row is
            // addressed by. Copying it produces a row the helper's own rule reads as
            // nameless, so the real name that arrives later has to fight it off instead
            // of simply filling a gap.
            var canonical = new ChatItem { Name = null };
            var transient = new ChatItem { Name = offered };

            Merge(canonical, transient);

            Assert.Null(canonical.Name);
        }

        [Fact]
        public void A_name_that_merely_contains_the_number_is_still_a_name()
        {
            // The number rule keys on the whole label. Widened to a substring it would
            // throw away the contacts people actually save that way.
            var canonical = new ChatItem { Name = null };
            var transient = new ChatItem { Name = "Ana 5511999990000" };

            Merge(canonical, transient);

            Assert.Equal("Ana 5511999990000", canonical.Name);
        }

        [Fact]
        public void A_row_wearing_the_other_address_is_nameless_too_and_accepts_the_real_name()
        {
            // Both rows are measured against both addresses. Judging each row only against
            // its own would leave this canonical row counting as named -- while wearing the
            // digits of the very LID it is being merged with -- and refusing the real name
            // the transient row brought.
            var canonical = new ChatItem { Name = "204837261509384" };
            var transient = new ChatItem { Name = "Ana Paula" };

            Merge(canonical, transient);

            Assert.Equal("Ana Paula", canonical.Name);
        }

        [Fact]
        public void A_number_written_with_punctuation_is_still_the_number()
        {
            // Plain equality against the address only catches the digits repeated verbatim.
            // A label is no more of a name for having been formatted, and ContactLabelSanitizer
            // already answers this question everywhere else.
            var canonical = new ChatItem { Name = null };
            var transient = new ChatItem { Name = "+55 11 99999-0000" };

            Merge(canonical, transient);

            Assert.Null(canonical.Name);
        }

        [Fact]
        public void Without_marker_rules_a_self_label_is_read_as_an_ordinary_name()
        {
            // Documents the fallback: spoof prevention is only as good as the injected
            // marker service, so a missing one has to fail towards leaving names alone.
            var canonical = new ChatItem { Name = "(You)" };
            var transient = new ChatItem { Name = "Ana Paula" };

            TransientChatMerge.Apply(canonical, transient, PhoneJid, LidJid, null);

            Assert.Equal("(You)", canonical.Name);
        }

        [Fact]
        public void A_row_with_no_address_at_all_still_merges()
        {
            // Rows recovered from a partial snapshot can reach here without a JID.
            var canonical = new ChatItem { Name = null };
            var transient = new ChatItem { Name = "Ana Paula" };

            TransientChatMerge.Apply(canonical, transient, null, null, new FakeSelfMarker());

            Assert.Equal("Ana Paula", canonical.Name);
        }

        // --- Guards --------------------------------------------------------------------

        [Fact]
        public void A_missing_row_on_either_side_is_not_an_error()
        {
            // The caller resolves both rows from a dictionary; either lookup can miss.
            var chat = new ChatItem { Name = "Ana Paula", UnreadCount = 2 };

            TransientChatMerge.Apply(null, chat, PhoneJid, LidJid, new FakeSelfMarker());
            TransientChatMerge.Apply(chat, null, PhoneJid, LidJid, new FakeSelfMarker());

            Assert.Equal("Ana Paula", chat.Name);
            Assert.Equal(2, chat.UnreadCount);
        }

        [Fact]
        public void A_row_merged_into_itself_is_left_exactly_as_it_was()
        {
            // Two aliases can resolve to the same row. Folding it into itself must not
            // count its unread twice or copy its preview onto itself mid-write.
            var chat = new ChatItem
            {
                Name = "Ana Paula",
                UnreadCount = 3,
                LastMessage = "Oi",
                LastMessageTimestampUtc = T0
            };

            TransientChatMerge.Apply(chat, chat, PhoneJid, PhoneJid, new FakeSelfMarker());

            Assert.Equal("Ana Paula", chat.Name);
            Assert.Equal(3, chat.UnreadCount);
            Assert.Equal("Oi", chat.LastMessage);
        }

        // --- AppendMissingMessages: the overlap -------------------------------------------

        private static ChatMessage Msg(string? id, int seconds = 0) =>
            new ChatMessage { Id = id, Timestamp = T0.AddSeconds(seconds), Content = id };

        [Fact]
        public void Everything_the_conversation_already_holds_is_skipped()
        {
            // Whatever arrived before the alias was known was written to whichever
            // address the sender used, so overlap is the normal case. Appending blindly
            // shows every one of those messages twice.
            var shared = Msg("m1");
            var canonical = new List<ChatMessage> { shared };
            var knownIds = new HashSet<string> { "m1" };

            TransientChatMerge.AppendMissingMessages(canonical, new[] { shared }, knownIds);

            Assert.Single(canonical);
        }

        [Fact]
        public void A_copy_of_a_known_message_is_recognised_by_its_id_not_its_identity()
        {
            // The two rows can hold separately deserialized objects for the same message.
            var canonical = new List<ChatMessage> { Msg("m1") };
            var knownIds = new HashSet<string> { "m1" };

            TransientChatMerge.AppendMissingMessages(canonical, new[] { Msg("m1") }, knownIds);

            Assert.Single(canonical);
        }

        [Fact]
        public void Messages_only_the_transient_row_saw_are_kept()
        {
            // The reason the merge touches messages at all.
            var canonical = new List<ChatMessage> { Msg("m1") };
            var knownIds = new HashSet<string> { "m1" };

            TransientChatMerge.AppendMissingMessages(
                canonical,
                new[] { Msg("m1"), Msg("m2", 10), Msg("m3", 20) },
                knownIds);

            Assert.Equal(new[] { "m1", "m2", "m3" }, canonical.ConvertAll(m => m.Id));
        }

        [Fact]
        public void The_same_message_listed_twice_still_lands_once()
        {
            // The transient list is not guaranteed to be deduplicated itself.
            var canonical = new List<ChatMessage>();

            TransientChatMerge.AppendMissingMessages(
                canonical,
                new[] { Msg("m1"), Msg("m1") },
                new HashSet<string>());

            Assert.Single(canonical);
        }

        [Fact]
        public void The_callers_id_index_learns_about_everything_that_was_added()
        {
            // The caller keeps using that index after the merge. Leaving it stale lets
            // the next batch re-add the very messages this call just appended.
            var canonical = new List<ChatMessage>();
            var knownIds = new HashSet<string>();

            TransientChatMerge.AppendMissingMessages(canonical, new[] { Msg("m1"), Msg("m2", 10) }, knownIds);

            Assert.True(knownIds.SetEquals(new[] { "m1", "m2" }));
        }

        // --- AppendMissingMessages: messages without an id -----------------------------------

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void A_message_with_no_id_is_matched_by_identity_instead(string? id)
        {
            // Not as strong as an id, but enough to stop a merge that runs twice from
            // duplicating rows, since both lists are built from the same objects.
            var orphan = new ChatMessage { Id = id, Content = "Oi", Timestamp = T0 };
            var canonical = new List<ChatMessage> { orphan };

            TransientChatMerge.AppendMissingMessages(canonical, new[] { orphan }, new HashSet<string>());

            Assert.Single(canonical);
        }

        [Fact]
        public void An_id_less_message_the_conversation_has_never_held_is_added()
        {
            var canonical = new List<ChatMessage>();

            TransientChatMerge.AppendMissingMessages(
                canonical,
                new[] { new ChatMessage { Content = "Oi", Timestamp = T0 } },
                new HashSet<string>());

            Assert.Single(canonical);
        }

        [Fact]
        public void An_id_less_message_never_pollutes_the_id_index()
        {
            // An empty string in the index would swallow the next id-less message.
            var knownIds = new HashSet<string>();

            TransientChatMerge.AppendMissingMessages(
                new List<ChatMessage>(),
                new[] { new ChatMessage { Id = null, Timestamp = T0 } },
                knownIds);

            Assert.Empty(knownIds);
        }

        // --- AppendMissingMessages: guards ----------------------------------------------------

        [Fact]
        public void Gaps_in_the_transient_list_are_skipped_rather_than_appended()
        {
            var canonical = new List<ChatMessage>();

            TransientChatMerge.AppendMissingMessages(
                canonical,
                new ChatMessage?[] { null, Msg("m1"), null },
                new HashSet<string>());

            Assert.Single(canonical);
            Assert.Equal("m1", canonical[0].Id);
        }

        [Fact]
        public void A_conversation_with_nothing_to_merge_is_not_an_error()
        {
            var canonical = new List<ChatMessage> { Msg("m1") };

            TransientChatMerge.AppendMissingMessages(canonical, null, new HashSet<string>());
            TransientChatMerge.AppendMissingMessages(null, new[] { Msg("m2") }, new HashSet<string>());

            Assert.Single(canonical);
        }

        [Fact]
        public void Without_an_id_index_one_is_built_rather_than_deduplication_being_skipped()
        {
            // A caller that passes no index wants deduplication, not the absence of it.
            // Reading the index as "nothing is known" would let through exactly the
            // duplicates this method exists to stop, and it would do it silently.
            var canonical = new List<ChatMessage> { Msg("m1") };

            TransientChatMerge.AppendMissingMessages(canonical, new[] { Msg("m1"), Msg("m2") });

            Assert.Equal(new[] { "m1", "m2" }, canonical.Select(m => m.Id));
        }
    }
}
