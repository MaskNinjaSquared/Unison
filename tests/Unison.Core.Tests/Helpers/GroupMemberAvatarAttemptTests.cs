using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class GroupMemberAvatarAttemptTests
    {
        [Fact]
        public void A_confirmed_miss_is_remembered()
        {
            var attempt = new GroupMemberAvatarAttempt();
            attempt.NoteNotFound();

            var result = attempt.Finish();

            Assert.True(result.IsNotFound);
            Assert.False(result.IsTransientFailure);
            Assert.Equal("no-picture", result.FailureReason);
        }

        [Fact]
        public void Any_transient_answer_beats_a_miss()
        {
            var missThenTimeout = new GroupMemberAvatarAttempt();
            missThenTimeout.NoteNotFound();
            missThenTimeout.NoteTimeout();
            var timeoutThenMiss = new GroupMemberAvatarAttempt();
            timeoutThenMiss.NoteTimeout();
            timeoutThenMiss.NoteNotFound();

            var first = missThenTimeout.Finish();
            var second = timeoutThenMiss.Finish();

            Assert.True(first.IsTransientFailure);
            Assert.False(first.IsNotFound);
            Assert.Equal("timeout", first.FailureReason);
            Assert.True(second.IsTransientFailure);
            Assert.Equal("no-picture", second.FailureReason);
        }

        [Fact]
        public void An_empty_download_and_a_blank_reason_stay_retryable()
        {
            var empty = new GroupMemberAvatarAttempt();
            empty.NoteNotFound();
            empty.NoteEmptyDownload();
            var blank = new GroupMemberAvatarAttempt();
            blank.NoteOtherFailure(null);

            Assert.Equal("download:empty", empty.Finish().FailureReason);
            Assert.Equal("no-url", blank.Finish().FailureReason);
            Assert.Equal("no-url", new GroupMemberAvatarAttempt().Finish().FailureReason);
        }

        [Fact]
        public void A_downloaded_picture_is_a_hit()
        {
            var result = new GroupMemberAvatarAttempt().Picture("ms-appdata:///local/m.jpg");

            Assert.True(result.HasPicture);
            Assert.Equal("ms-appdata:///local/m.jpg", result.LocalUri);
        }
    }
}
