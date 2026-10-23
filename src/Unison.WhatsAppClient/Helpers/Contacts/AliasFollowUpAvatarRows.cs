// =============================================================================
// AliasFollowUpAvatarRows
//
// After a LID/PN burst, which list rows still have no avatar and should get a
// forced retry. Pure selection; the host raises OnAvatarRetryDue.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Helpers
{
    public static class AliasFollowUpAvatarRows
    {
        /// <summary>
        /// Rows under the given phone JIDs that still have a blank avatar URL.
        /// </summary>
        public static List<ChatItem> CollectMissing(
            IEnumerable<string> phoneJids,
            Func<string, IEnumerable<ChatItem>> rowsForCanonical)
        {
            var rows = new List<ChatItem>();
            if (phoneJids == null || rowsForCanonical == null)
            {
                return rows;
            }

            foreach (string pn in phoneJids)
            {
                IEnumerable<ChatItem> candidates = rowsForCanonical(pn);
                if (candidates == null)
                {
                    continue;
                }

                foreach (ChatItem chat in candidates)
                {
                    if (chat != null && string.IsNullOrWhiteSpace(chat.AvatarUrl))
                    {
                        rows.Add(chat);
                    }
                }
            }

            return rows;
        }
    }
}
