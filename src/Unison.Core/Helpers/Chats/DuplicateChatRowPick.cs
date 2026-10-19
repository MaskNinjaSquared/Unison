// =============================================================================
// DuplicateChatRowPick
//
// While one person can still occupy two chat rows (phone JID and LID), callers
// that must point at a single row need a rule for which one. Two questions, and
// they do not share an answer: which row should the list move to the top, and
// which name is already good enough to show.
// =============================================================================
using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public static class DuplicateChatRowPick
    {
        /// <summary>
        /// The row to reposition after a local send. A picture beats a name, and a
        /// name beats a blank row. Ties keep the earlier row.
        /// </summary>
        public static ChatItem PreferredForPreview(IReadOnlyList<ChatItem> rows)
        {
            if (rows == null || rows.Count == 0)
            {
                return null;
            }

            ChatItem best = null;
            bool bestHasAvatar = false;
            bool bestHasName = false;
            for (int i = 0; i < rows.Count; i++)
            {
                ChatItem row = rows[i];
                bool hasAvatar = row != null && !string.IsNullOrWhiteSpace(row.AvatarUrl);
                bool hasName = row != null && !string.IsNullOrWhiteSpace(row.Name);
                bool betterAvatar = hasAvatar && !bestHasAvatar;
                bool sameAvatarBetterName = hasAvatar == bestHasAvatar && hasName && !bestHasName;
                if (best == null || betterAvatar || sameAvatarBetterName)
                {
                    best = row;
                    bestHasAvatar = hasAvatar;
                    bestHasName = hasName;
                }
            }

            return best;
        }

        /// <summary>The first non-blank name among the rows, in the order given.</summary>
        public static string FirstNonBlankName(IReadOnlyList<ChatItem> rows)
        {
            if (rows == null)
            {
                return null;
            }

            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i] != null && !string.IsNullOrWhiteSpace(rows[i].Name))
                {
                    return rows[i].Name;
                }
            }

            return null;
        }
    }
}
