// =============================================================================
// Tests for ChatAvatarOutcome.
//
// What separates "has no photo" from "could not reach it". Both look like an
// empty circle; only one of them should ever be asked about again.
// =============================================================================
using System;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ChatAvatarOutcomeTests
    {
        private static readonly DateTime Now = new DateTime(2026, 4, 2, 8, 30, 0, DateTimeKind.Utc);
        private static readonly DateTime Earlier = Now.AddHours(-6);

        private static ChatItem WithPhoto() =>
            new ChatItem
            {
                AvatarUrl = "ms-appdata:///local/MediaCache/Avatars/ana.jpg",
                AvatarFetchedAtUtc = Earlier
            };

        // --- RecordCached -----------------------------------------------------

        [Fact]
        public void A_cached_image_is_shown_and_marked_answered()
        {
            var chat = new ChatItem();

            ChatAvatarOutcome.RecordCached(chat, "ms-appdata:///local/a.jpg", Now);

            Assert.Equal("ms-appdata:///local/a.jpg", chat.AvatarUrl);
            Assert.Equal(Now, chat.AvatarFetchedAtUtc);
        }

        [Fact]
        public void Succeeding_wipes_the_record_of_an_earlier_failure()
        {
            // Otherwise a contact who was once unreachable keeps a failure reason
            // attached to a photo that is now showing fine.
            var chat = new ChatItem
            {
                AvatarFetchFailedAtUtc = Earlier,
                AvatarFetchFailureReason = "timeout"
            };

            ChatAvatarOutcome.RecordCached(chat, "ms-appdata:///local/a.jpg", Now);

            Assert.Null(chat.AvatarFetchFailedAtUtc);
            Assert.Null(chat.AvatarFetchFailureReason);
        }

        // --- RecordAbsent -----------------------------------------------------

        [Fact]
        public void Having_no_photo_counts_as_an_answer()
        {
            // The row must stop being re-asked, which is what the fetch stamp means.
            var chat = new ChatItem();

            ChatAvatarOutcome.RecordAbsent(chat, "no-picture", Now);

            Assert.Equal(Now, chat.AvatarFetchedAtUtc);
            Assert.Null(chat.AvatarFetchFailedAtUtc);
        }

        [Fact]
        public void A_photo_the_user_removed_stops_being_shown()
        {
            var chat = WithPhoto();

            ChatAvatarOutcome.RecordAbsent(chat, "no-picture", Now);

            Assert.Null(chat.AvatarUrl);
        }

        [Fact]
        public void The_reason_is_kept_even_though_this_is_not_a_failure()
        {
            var chat = new ChatItem();

            ChatAvatarOutcome.RecordAbsent(chat, "no-picture:group-fallback-miss", Now);

            Assert.Equal("no-picture:group-fallback-miss", chat.AvatarFetchFailureReason);
        }

        // --- RecordFailure ----------------------------------------------------

        [Fact]
        public void A_failure_leaves_the_row_looking_unanswered_so_it_is_retried()
        {
            // The whole point: not stamping the fetch time is what allows another pass.
            var chat = new ChatItem();

            ChatAvatarOutcome.RecordFailure(chat, "timeout", Now);

            Assert.Null(chat.AvatarFetchedAtUtc);
            Assert.Equal(Now, chat.AvatarFetchFailedAtUtc);
            Assert.Equal("timeout", chat.AvatarFetchFailureReason);
        }

        [Fact]
        public void A_failure_never_takes_away_a_picture_that_is_already_showing()
        {
            // A blank circle is worse than a slightly old photo.
            var chat = WithPhoto();

            ChatAvatarOutcome.RecordFailure(chat, "download:host unreachable", Now);

            Assert.Equal("ms-appdata:///local/MediaCache/Avatars/ana.jpg", chat.AvatarUrl);
            Assert.Equal(Earlier, chat.AvatarFetchedAtUtc);
        }

        [Fact]
        public void Failing_twice_records_the_most_recent_attempt()
        {
            var chat = new ChatItem();

            ChatAvatarOutcome.RecordFailure(chat, "timeout", Earlier);
            ChatAvatarOutcome.RecordFailure(chat, "download:empty", Now);

            Assert.Equal(Now, chat.AvatarFetchFailedAtUtc);
            Assert.Equal("download:empty", chat.AvatarFetchFailureReason);
        }

        // --- The distinction the whole type exists for -------------------------

        [Fact]
        public void Absent_and_failed_disagree_about_being_asked_again()
        {
            var absent = new ChatItem();
            var failed = new ChatItem();

            ChatAvatarOutcome.RecordAbsent(absent, "no-picture", Now);
            ChatAvatarOutcome.RecordFailure(failed, "no-picture", Now);

            Assert.NotNull(absent.AvatarFetchedAtUtc);
            Assert.Null(failed.AvatarFetchedAtUtc);
        }

        [Fact]
        public void None_of_the_outcomes_trip_over_a_missing_row()
        {
            ChatAvatarOutcome.RecordCached(null, "a", Now);
            ChatAvatarOutcome.RecordAbsent(null, "b", Now);
            ChatAvatarOutcome.RecordFailure(null, "c", Now);
        }
    }
}
