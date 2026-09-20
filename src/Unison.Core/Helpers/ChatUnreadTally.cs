// =============================================================================
// ChatUnreadTally
//
// A conversation's unread count is not stored once. The same conversation can
// be listed under both its PN and its LID form, so the number lives mirrored
// across several rows, and the badge the user sees is whichever row the list
// happens to render.
//
// That is why a count is never read from a single row and never incremented in
// place: every write reads the highest value among the sibling rows and stamps
// that one result onto all of them. Reading one row instead would let a message
// arriving on the LID row leave the PN row a number behind, and the badge would
// then depend on which row won the sort.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public static class ChatUnreadTally
    {
        /// <summary>
        /// The count a conversation is currently showing, taken as the highest among the
        /// rows that share its identity. Never negative.
        /// </summary>
        /// <remarks>
        /// The highest wins rather than the newest because the rows are not updated in
        /// lockstep: a row that missed a mutation is stale, not authoritative, and taking
        /// the lower value would silently drop unread messages.
        /// </remarks>
        public static int HighestAmong(IReadOnlyList<ChatItem> rows)
        {
            int highest = 0;
            if (rows != null)
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    ChatItem row = rows[i];
                    if (row != null && row.UnreadCount > highest)
                    {
                        highest = row.UnreadCount;
                    }
                }
            }

            return highest;
        }

        /// <summary>
        /// Adds <paramref name="delta"/> to a conversation's unread count and writes the
        /// result onto every row of it. Returns the new count.
        /// </summary>
        /// <param name="preferred">
        /// The row the caller is holding. It is the starting point only when no sibling
        /// rows were found; otherwise the siblings decide and this row is merely written
        /// to, which keeps a stale row from dragging the count down.
        /// </param>
        public static int Bump(ChatItem preferred, IReadOnlyList<ChatItem> rows, int delta)
        {
            bool hasRows = rows != null && rows.Count > 0;

            int current = hasRows
                ? HighestAmong(rows)
                : (preferred != null ? Math.Max(0, preferred.UnreadCount) : 0);

            int next = Math.Max(0, current + delta);

            if (hasRows)
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    if (rows[i] != null)
                    {
                        rows[i].UnreadCount = next;
                    }
                }
            }

            if (preferred != null)
            {
                preferred.UnreadCount = next;
            }

            return next;
        }
    }
}
