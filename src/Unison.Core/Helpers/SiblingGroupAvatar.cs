// =============================================================================
// SiblingGroupAvatar
//
// A group the user is in twice -- the same conversation reached through two
// JIDs -- where one of the two rows already has the picture.
//
// This existed twice: once in the fetch path that copies the picture, and once
// in the policy that decides whether a retry is worth scheduling. The two did
// not agree on what counts as "has a picture". The policy accepted a sibling
// whose only picture was the high-resolution one; the fetch copies AvatarUrl
// and would have copied nothing.
//
// That disagreement did not just miss a picture, it span: the policy kept
// asking for a retry it had proof was worthwhile, the fetch kept refusing, and
// the row went back in the queue on every pass -- two IQs a round, forever.
//
// So the answer lives here once, and it is the restrictive one, because the
// caller that acts on it can only copy what it can read.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public static class SiblingGroupAvatar
    {
        /// <summary>
        /// Another row for the same group, by subject, that has a picture this one can take.
        /// </summary>
        /// <remarks>
        /// Matching on the subject is deliberate: the two rows have different addresses by
        /// definition — that is what makes them siblings rather than the same row — so the
        /// name is the only thing left that ties them together.
        /// </remarks>
        public static ChatItem Find(ChatItem chat, IEnumerable<ChatItem> chats)
        {
            if (chat == null || chats == null || !chat.IsGroup)
            {
                return null;
            }

            string targetName = (chat.Name ?? string.Empty).Trim();
            if (targetName.Length == 0)
            {
                return null;
            }

            string targetJid = JidHelper.Normalize(chat.JID);

            foreach (ChatItem candidate in chats)
            {
                if (candidate == null || !candidate.IsGroup || ReferenceEquals(candidate, chat))
                {
                    continue;
                }

                if (string.Equals(JidHelper.Normalize(candidate.JID), targetJid, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.Equals((candidate.Name ?? string.Empty).Trim(), targetName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // What the copier reads. A sibling holding only the high-resolution file has
                // nothing to hand over, and promising it starts the loop described above.
                if (!string.IsNullOrWhiteSpace(candidate.AvatarUrl))
                {
                    return candidate;
                }
            }

            return null;
        }
    }
}
