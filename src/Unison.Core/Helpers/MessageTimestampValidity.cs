// =============================================================================
// MessageTimestampValidity
//
// Whether an incoming message's timestamp can be believed.
//
// Two ways a bad one arrives: a server event replayed without a timestamp at
// all, and a value so far from now that it would sort to the top or bottom of
// the conversation forever. Neither is repaired by guessing -- a rejected
// timestamp becomes DateTime.MinValue, which the pipeline reads as "no time
// known", rather than DateTime.UtcNow, which would silently turn a replayed old
// event into the newest message in the chat.
// =============================================================================
using System;

namespace Unison.Core.Helpers
{
    public static class MessageTimestampValidity
    {
        /// <summary>
        /// Nothing genuine predates WhatsApp itself, so anything earlier is a decoding
        /// accident rather than an old message.
        /// </summary>
        public const int EarliestPlausibleYear = 2009;

        /// <summary>
        /// How far ahead of us a timestamp may sit and still be accepted. Clock skew
        /// between the phone, the server and the companion is real and one-sided
        /// rejection would drop legitimate messages; two days absorbs that without
        /// admitting a value that would pin itself to the top of the list.
        /// </summary>
        public static readonly TimeSpan FutureTolerance = TimeSpan.FromDays(2);

        /// <summary>
        /// Whether <paramref name="timestamp"/> is usable as a message time.
        /// </summary>
        /// <param name="utcNow">
        /// Passed in rather than read from the clock so the boundaries are testable.
        /// </param>
        /// <remarks>
        /// Both sides are normalized before comparing. Timestamps reaching this rule come
        /// from the socket as <see cref="DateTimeKind.Utc"/> and from SQLite as
        /// <see cref="DateTimeKind.Unspecified"/>, and comparing those raw is off by the
        /// local offset -- which, near the future cut-off, decides acceptance.
        /// </remarks>
        public static bool IsValid(DateTime timestamp, DateTime utcNow)
        {
            if (timestamp == DateTime.MinValue)
            {
                return false;
            }

            DateTime comparable = ChatMessageOrder.ToComparableUtc(timestamp);

            return comparable.Year >= EarliestPlausibleYear &&
                   comparable <= ChatMessageOrder.ToComparableUtc(utcNow) + FutureTolerance;
        }

        /// <summary>
        /// The timestamp to store: the original when it is believable, otherwise
        /// <see cref="DateTime.MinValue"/>.
        /// </summary>
        /// <remarks>
        /// Returns the value unchanged rather than the normalized one, so this rule stays
        /// a filter and does not quietly become a converter.
        /// </remarks>
        public static DateTime KeepOrDiscard(DateTime timestamp, DateTime utcNow)
        {
            return IsValid(timestamp, utcNow) ? timestamp : DateTime.MinValue;
        }
    }
}
