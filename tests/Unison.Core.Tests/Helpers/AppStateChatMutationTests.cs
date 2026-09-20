// =============================================================================
// Tests for AppStateChatMutation.
//
// Mutations arriving from the account. They land on every row sharing a
// canonical identity, so most of what is pinned here is about rows disagreeing
// with each other.
// =============================================================================
using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class AppStateChatMutationTests
    {
        private const long Now = 1_700_000_000_000L;

        private static List<ChatItem> Rows(params int[] unreadCounts)
        {
            var rows = new List<ChatItem>();
            foreach (int count in unreadCounts)
            {
                rows.Add(new ChatItem { UnreadCount = count });
            }

            return rows;
        }

        // --- ResolveUnreadCount ----------------------------------------------

        [Fact]
        public void Marking_read_clears_the_count()
        {
            Assert.Equal(0, AppStateChatMutation.ResolveUnreadCount(Rows(7), read: true));
        }

        [Fact]
        public void Marking_read_clears_it_however_many_rows_disagree()
        {
            Assert.Equal(0, AppStateChatMutation.ResolveUnreadCount(Rows(7, 0, 3), read: true));
        }

        [Fact]
        public void Marking_unread_keeps_the_best_count_any_row_still_knows()
        {
            // The mutation carries no number. One alias row may still remember how many
            // messages were actually waiting, and that beats inventing a 1.
            Assert.Equal(7, AppStateChatMutation.ResolveUnreadCount(Rows(0, 7, 3), read: false));
        }

        [Fact]
        public void Marking_unread_on_an_already_read_chat_still_shows_something()
        {
            // Otherwise "mark as unread" would appear to do nothing.
            Assert.Equal(1, AppStateChatMutation.ResolveUnreadCount(Rows(0, 0), read: false));
        }

        [Fact]
        public void A_negative_count_never_becomes_the_answer()
        {
            Assert.Equal(1, AppStateChatMutation.ResolveUnreadCount(Rows(-4), read: false));
        }

        [Fact]
        public void With_no_rows_at_all_the_answer_is_still_coherent()
        {
            Assert.Equal(0, AppStateChatMutation.ResolveUnreadCount(Rows(), read: true));
            Assert.Equal(1, AppStateChatMutation.ResolveUnreadCount(Rows(), read: false));
            Assert.Equal(1, AppStateChatMutation.ResolveUnreadCount(null, read: false));
        }

        // --- ApplyFlags -------------------------------------------------------

        [Fact]
        public void A_mutation_leaves_alone_everything_it_did_not_mention()
        {
            var chat = new ChatItem
            {
                IsArchived = true,
                IsChatPinned = true,
                PinnedTimestamp = 42,
                MutedUntil = 99
            };

            AppStateChatMutation.ApplyFlags(chat, new ChatFlagChange(), Now);

            Assert.True(chat.IsArchived);
            Assert.True(chat.IsChatPinned);
            Assert.Equal(42, chat.PinnedTimestamp);
            Assert.Equal(99, chat.MutedUntil);
        }

        [Fact]
        public void Archiving_and_unarchiving_both_take()
        {
            var chat = new ChatItem();

            AppStateChatMutation.ApplyFlags(chat, new ChatFlagChange { Archived = true }, Now);
            Assert.True(chat.IsArchived);

            AppStateChatMutation.ApplyFlags(chat, new ChatFlagChange { Archived = false }, Now);
            Assert.False(chat.IsArchived);
        }

        [Fact]
        public void A_pin_carrying_its_own_timestamp_uses_it()
        {
            var chat = new ChatItem();

            AppStateChatMutation.ApplyFlags(
                chat,
                new ChatFlagChange { Pinned = true, PinnedTimestamp = 555 },
                Now);

            Assert.True(chat.IsChatPinned);
            Assert.Equal(555, chat.PinnedTimestamp);
        }

        [Fact]
        public void A_pin_without_one_keeps_the_moment_the_chat_was_first_pinned()
        {
            // Re-pinning must not jump the chat above others pinned later.
            var chat = new ChatItem { PinnedTimestamp = 111 };

            AppStateChatMutation.ApplyFlags(chat, new ChatFlagChange { Pinned = true }, Now);

            Assert.Equal(111, chat.PinnedTimestamp);
        }

        [Fact]
        public void A_first_pin_with_nothing_to_go_on_is_stamped_now()
        {
            var chat = new ChatItem();

            AppStateChatMutation.ApplyFlags(chat, new ChatFlagChange { Pinned = true }, Now);

            Assert.Equal(Now, chat.PinnedTimestamp);
        }

        [Fact]
        public void An_unpin_is_written_as_zero_rather_than_as_absence()
        {
            // An alias row that has not received this mutation yet would otherwise
            // resurrect the pin through dedupe. Zero says it was deliberate.
            var chat = new ChatItem { IsChatPinned = true, PinnedTimestamp = 111 };

            AppStateChatMutation.ApplyFlags(chat, new ChatFlagChange { Pinned = false }, Now);

            Assert.False(chat.IsChatPinned);
            Assert.Equal(0, chat.PinnedTimestamp);
        }

        [Fact]
        public void Muting_writes_the_end_timestamp()
        {
            var chat = new ChatItem();

            AppStateChatMutation.ApplyFlags(
                chat,
                new ChatFlagChange { AppliesMute = true, MuteEndTimestamp = 888 },
                Now);

            Assert.Equal(888, chat.MutedUntil);
        }

        [Fact]
        public void Unmuting_clears_it_even_though_that_value_is_null()
        {
            // The reason AppliesMute exists: null is a value here, not an absence.
            var chat = new ChatItem { MutedUntil = 888 };

            AppStateChatMutation.ApplyFlags(
                chat,
                new ChatFlagChange { AppliesMute = true, MuteEndTimestamp = null },
                Now);

            Assert.Null(chat.MutedUntil);
        }

        [Fact]
        public void A_mute_timestamp_without_the_flag_is_ignored()
        {
            var chat = new ChatItem { MutedUntil = 888 };

            AppStateChatMutation.ApplyFlags(
                chat,
                new ChatFlagChange { MuteEndTimestamp = 111 },
                Now);

            Assert.Equal(888, chat.MutedUntil);
        }

        [Fact]
        public void Muted_forever_arrives_as_zero_and_is_kept_as_zero()
        {
            var chat = new ChatItem();

            AppStateChatMutation.ApplyFlags(
                chat,
                new ChatFlagChange { AppliesMute = true, MuteEndTimestamp = 0 },
                Now);

            Assert.Equal(0, chat.MutedUntil);
        }

        [Fact]
        public void One_mutation_can_carry_several_flags_at_once()
        {
            var chat = new ChatItem();

            AppStateChatMutation.ApplyFlags(
                chat,
                new ChatFlagChange
                {
                    Archived = true,
                    Pinned = true,
                    PinnedTimestamp = 555,
                    AppliesMute = true,
                    MuteEndTimestamp = 888
                },
                Now);

            Assert.True(chat.IsArchived);
            Assert.True(chat.IsChatPinned);
            Assert.Equal(555, chat.PinnedTimestamp);
            Assert.Equal(888, chat.MutedUntil);
        }

        [Fact]
        public void A_missing_row_is_not_an_error()
        {
            AppStateChatMutation.ApplyFlags(null, new ChatFlagChange { Archived = true }, Now);
        }

        // --- TouchesAnything --------------------------------------------------

        [Fact]
        public void A_mutation_that_changes_no_flag_is_not_worth_a_store_write()
        {
            Assert.False(new ChatFlagChange().TouchesAnything);
            Assert.False(new ChatFlagChange { MuteEndTimestamp = 5 }.TouchesAnything);
        }

        [Fact]
        public void Any_flag_at_all_is_worth_a_store_write()
        {
            Assert.True(new ChatFlagChange { Archived = false }.TouchesAnything);
            Assert.True(new ChatFlagChange { Pinned = false }.TouchesAnything);
            Assert.True(new ChatFlagChange { AppliesMute = true }.TouchesAnything);
        }
    }
}
