// =============================================================================
// MessageMentionsSelf
//
// True when a message cites this account (@jid) or the whole group (@all /
// NonJidMentions). Used for the chat-list HasCitation badge.
// =============================================================================
using System;
using System.Collections.Generic;

namespace Unison.Core.Helpers
{
    public static class MessageMentionsSelf
    {
        /// <summary>
        /// <paramref name="isSelfLinked"/> must recognise every self address (PN, LID, aliases).
        /// </summary>
        public static bool Check(
            bool mentionAll,
            IEnumerable<string> mentionedJids,
            Func<string, bool> isSelfLinked)
        {
            if (mentionAll)
            {
                return true;
            }

            if (mentionedJids == null || isSelfLinked == null)
            {
                return false;
            }

            foreach (string jid in mentionedJids)
            {
                if (string.IsNullOrWhiteSpace(jid))
                {
                    continue;
                }

                if (isSelfLinked(jid))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
