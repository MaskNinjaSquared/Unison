// =============================================================================
// ChatListAddressBookTargets
//
// The chat-list rows the startup address-book pass looks up. Only 1:1 chats:
// a group has no entry in the user's agenda, and its members are named later,
// once the session is up.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public static class ChatListAddressBookTargets
    {
        /// <param name="canonicalize">
        /// The alias map's canonical address. The agenda is matched by phone, so a LID row has to be
        /// looked up under its phone address.
        /// </param>
        public static List<string> Select(IEnumerable<ChatItem> chats, Func<string, string> canonicalize)
        {
            var targets = new List<string>();
            if (chats == null)
            {
                return targets;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ChatItem chat in chats)
            {
                if (chat == null || chat.IsGroup || string.IsNullOrWhiteSpace(chat.JID))
                {
                    continue;
                }

                string jid = JidHelper.Normalize(chat.JID);
                if (string.IsNullOrEmpty(jid) || JidHelper.IsGroupJid(jid))
                {
                    continue;
                }

                string canonical = canonicalize?.Invoke(jid);
                string target = string.IsNullOrWhiteSpace(canonical) ? jid : JidHelper.Normalize(canonical);
                if (seen.Add(target))
                {
                    targets.Add(target);
                }
            }

            return targets;
        }
    }
}
