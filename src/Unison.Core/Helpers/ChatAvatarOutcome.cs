// =============================================================================
// ChatAvatarOutcome
//
// Records how an avatar lookup ended on the chat row. Four fields, three
// outcomes, and the difference between two of them decides whether the app
// ever asks again.
//
// "This account has no photo" is an answer, and gets stamped as one. "I could
// not reach it" is not, and must leave the row looking unanswered so a later
// pass retries. Collapsing the two in either direction is a real bug with no
// error attached: one way the contact shows a blank circle forever, the other
// way the app re-asks the server on every sweep.
// =============================================================================
using System;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public static class ChatAvatarOutcome
    {
        /// <summary>
        /// An image was fetched and cached locally.
        /// </summary>
        public static void RecordCached(ChatItem chat, string localUri, DateTime nowUtc)
        {
            if (chat == null)
            {
                return;
            }

            chat.AvatarUrl = localUri;
            chat.AvatarFetchedAtUtc = nowUtc;
            chat.AvatarFetchFailedAtUtc = null;
            chat.AvatarFetchFailureReason = null;
        }

        /// <summary>
        /// The account was reached and genuinely has no picture.
        /// </summary>
        /// <remarks>
        /// Counts as answered, so the row stops being re-asked. The existing image is
        /// dropped: it would otherwise outlive a photo the user deliberately removed. The
        /// reason is kept for diagnostics even though this is not a failure.
        /// </remarks>
        public static void RecordAbsent(ChatItem chat, string reason, DateTime nowUtc)
        {
            if (chat == null)
            {
                return;
            }

            chat.AvatarUrl = null;
            chat.AvatarFetchedAtUtc = nowUtc;
            chat.AvatarFetchFailedAtUtc = null;
            chat.AvatarFetchFailureReason = reason;
        }

        /// <summary>
        /// The lookup did not complete — a timeout, a download error, or a transient
        /// refusal.
        /// </summary>
        /// <remarks>
        /// Deliberately leaves <c>AvatarUrl</c> and <c>AvatarFetchedAtUtc</c> untouched.
        /// Whatever the row is already showing is better than a blank circle, and not
        /// stamping the fetch time is what lets a later pass try again.
        /// </remarks>
        public static void RecordFailure(ChatItem chat, string reason, DateTime nowUtc)
        {
            if (chat == null)
            {
                return;
            }

            chat.AvatarFetchFailedAtUtc = nowUtc;
            chat.AvatarFetchFailureReason = reason;
        }
    }
}
