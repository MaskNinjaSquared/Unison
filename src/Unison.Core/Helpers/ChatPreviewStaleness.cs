// =============================================================================
// ChatPreviewStaleness
//
// Whether a candidate preview is allowed to replace the one a chat row is
// showing, judged on time alone.
//
// Previews arrive from several directions at once — live messages, history
// catch-up, offline replay, reconciliation against SQLite — and they do not
// arrive in order. Without this gate, a history chunk delivered after a live
// message would overwrite the list with something older, which reads as the
// conversation going backwards.
// =============================================================================
using System;

namespace Unison.Core.Helpers
{
    public static class ChatPreviewStaleness
    {
        /// <param name="force">
        /// Set by callers that have already established the candidate is the newest thing
        /// available, having compared every source themselves. Skips the gate entirely,
        /// because a row whose stored instant is wrong would otherwise refuse the very
        /// correction meant to fix it.
        /// </param>
        public static bool ShouldAccept(DateTime? currentTimestampUtc, DateTime candidateTimestamp, bool force)
        {
            if (force)
            {
                return true;
            }

            DateTime candidateUtc = ChatMessageOrder.ToComparableUtc(candidateTimestamp);
            if (candidateUtc == DateTime.MinValue)
            {
                // A message with no usable instant still belongs in its conversation, but it
                // cannot be trusted to relabel the row or move it up the list.
                return false;
            }

            if (!currentTimestampUtc.HasValue)
            {
                return true;
            }

            DateTime currentUtc = ChatMessageOrder.ToComparableUtc(currentTimestampUtc.Value);
            if (currentUtc == DateTime.MinValue)
            {
                return true;
            }

            // Equal instants are accepted: the same message often arrives again carrying more
            // than it did the first time — a delivery receipt, a caption, a resolved author.
            return candidateUtc >= currentUtc;
        }
    }
}
