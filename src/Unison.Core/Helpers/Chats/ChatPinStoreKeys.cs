using System;
using System.Collections.Generic;
using Unison.Core.Contracts;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Every address one person is filed under: the row JID, its canonical form, and the
    /// other side of the PN/LID pair. Pins, history reads and avatar lookups all use it.
    /// Writing and reading only the row JID leaves the other spelling behind.
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
                // The table stores normalized keys. A canonical answer can still be the raw
                // ":device" form, and asking with that misses the pair.
                if (jids.TryGetAlias(NormalizeForLookup(jid), out alias))
                {
                    Add(keys, alias);
                }

                if (jids.TryGetAlias(NormalizeForLookup(canonical), out alias))
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

        /// <summary>
        /// The keys of <paramref name="first"/>, then each key of <paramref name="second"/> not
        /// already among them, so a store write per key never repeats one.
        /// </summary>
        public static IReadOnlyList<string> Merge(IReadOnlyList<string> first, IReadOnlyList<string> second)
        {
            var keys = new List<string>((first?.Count ?? 0) + (second?.Count ?? 0));
            AddAll(keys, first);
            AddAll(keys, second);
            return keys;
        }

        private static void AddAll(List<string> keys, IReadOnlyList<string> source)
        {
            if (source == null)
            {
                return;
            }

            for (int i = 0; i < source.Count; i++)
            {
                Add(keys, source[i]);
            }
        }

        private static string NormalizeForLookup(string jid)
        {
            string normalized = JidHelper.Normalize(jid);
            return string.IsNullOrWhiteSpace(normalized) ? string.Empty : normalized;
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
