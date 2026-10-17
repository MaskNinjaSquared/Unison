using Unison.Core.State;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// An avatar already on a chat row for this person, so a roster can show it without asking
    /// the directory again.
    /// </summary>
    /// <remarks>
    /// A member can arrive under a LID while the 1:1 chat is filed under the phone number, or the
    /// other way round; each address the reply carried is tried in turn.
    /// </remarks>
    public static class ChatRowAvatarLookup
    {
        /// <returns>The first non-blank avatar URL, or null.</returns>
        public static string Find(ChatRowIndex rows, string jid, string phoneNumber, string lid)
        {
            return OnRows(rows, jid) ??
                   OnRows(rows, JidHelper.Normalize(phoneNumber)) ??
                   OnRows(rows, JidHelper.Normalize(lid));
        }

        private static string OnRows(ChatRowIndex rows, string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return null;
            }

            foreach (var row in rows.FindConversation(jid))
            {
                if (row != null && !string.IsNullOrWhiteSpace(row.AvatarUrl))
                {
                    return row.AvatarUrl;
                }
            }

            return null;
        }
    }
}
