using System;
using System.Collections.Generic;
using Unison.Core.Contracts;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// JIDs under which a chat-list pin may be stored or looked up. App-state
    /// <c>pin_v1</c> indexes one address; history rows often use the other (PN vs LID).
    /// Writing and reading only the row JID leaves the pin invisible after sync.
    /// </summary>
    public static class ChatPinStoreKeys
    {
        public static IReadOnlyList<string> For(string jid, IJidResolver jids)
        {
            var keys = new List<string>(3);
            Add(keys, jid);

            if (jids != null)
            {
                string canonical = jids.GetCanonicalJid(jid);
                Add(keys, canonical);

                string alias;
                if (jids.TryGetAlias(jid, out alias))
                {
                    Add(keys, alias);
                }

                if (!string.IsNullOrWhiteSpace(canonical) &&
                    jids.TryGetAlias(canonical, out alias))
                {
                    Add(keys, alias);
                }
            }

            return keys;
        }

        public static IReadOnlyList<string> For(
            string jid,
            string canonicalJid,
            string aliasJid)
        {
            var keys = new List<string>(3);
            Add(keys, jid);
            Add(keys, canonicalJid);
            Add(keys, aliasJid);
            return keys;
        }

        private static void Add(List<string> keys, string jid)
        {
            string normalized = JidHelper.Normalize(jid);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            for (int i = 0; i < keys.Count; i++)
            {
                if (string.Equals(keys[i], normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            keys.Add(normalized);
        }
    }
}
