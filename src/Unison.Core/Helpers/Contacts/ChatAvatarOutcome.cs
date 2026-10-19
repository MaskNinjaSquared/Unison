// =============================================================================
// ChatAvatarOutcome
//
// Records how an avatar lookup ended. Four fields, three outcomes, and the
// difference between two of them decides whether the app ever asks again.
//
// "This account has no photo" is an answer, and gets stamped as one. "I could
// not reach it" is not, and must leave the subject looking unanswered so a
// later pass retries. Collapsing the two in either direction is a real bug with
// no error attached: one way the contact shows a blank circle forever, the
// other way the app re-asks the server on every sweep.
//
// Applies to chat rows and to group participants alike. They had separate
// copies of this, and the group one kept the old picture on a confirmed miss --
// which, because a cached url suppresses the next lookup entirely, meant a
// participant who removed their photo went on showing the old one forever.
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
        public static void RecordCached(IAvatarSubject subject, string localUri, DateTime nowUtc)
        {
            if (subject == null)
            {
                return;
            }

            subject.AvatarUrl = localUri;
            subject.AvatarFetchedAtUtc = nowUtc;
            subject.AvatarFetchFailedAtUtc = null;
            subject.AvatarFetchFailureReason = null;
        }

        /// <summary>
        /// The account was reached and genuinely has no picture.
        /// </summary>
        /// <remarks>
        /// Counts as answered, so the subject stops being re-asked. The existing image is
        /// dropped: it would otherwise outlive a photo the user deliberately removed, and
        /// a surviving url suppresses every later lookup, so the stale picture would be
        /// permanent. The reason is kept for diagnostics even though this is not a failure.
        /// </remarks>
        public static void RecordAbsent(IAvatarSubject subject, string reason, DateTime nowUtc)
        {
            if (subject == null)
            {
                return;
            }

            subject.AvatarUrl = null;
            subject.AvatarFetchedAtUtc = nowUtc;
            subject.AvatarFetchFailedAtUtc = null;
            subject.AvatarFetchFailureReason = reason;
        }

        /// <summary>
        /// The lookup did not complete — a timeout, a download error, or a transient
        /// refusal.
        /// </summary>
        /// <remarks>
        /// Deliberately leaves <c>AvatarUrl</c> and <c>AvatarFetchedAtUtc</c> untouched.
        /// Whatever is already showing is better than a blank circle, and not stamping the
        /// fetch time is what lets a later pass try again.
        /// </remarks>
        public static void RecordFailure(IAvatarSubject subject, string reason, DateTime nowUtc)
        {
            if (subject == null)
            {
                return;
            }

            subject.AvatarFetchFailedAtUtc = nowUtc;
            subject.AvatarFetchFailureReason = reason;
        }
    }
}
