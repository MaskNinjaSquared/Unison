// =============================================================================
// SessionOpenOnDemandPendingList
//
// Durable crash latch for session-open / tip-only HISTORY_SYNC_ON_DEMAND: a
// capped list of canonical JIDs. -1/0/1 completeness columns are intentionally
// avoided — this only remembers "request was in flight when we died".
// =============================================================================
using System;
using System.Collections.Generic;

namespace Unison.Core.Helpers.History
{
    public static class SessionOpenOnDemandPendingList
    {
        /// <summary>Hard cap so a reconnect cannot fan out dozens of OnDemand pages.</summary>
        public const int Cap = 12;

        public static List<string> Parse(string raw)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return result;
            }

            string[] parts = raw.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string jid = parts[i].Trim();
                if (jid.Length == 0)
                {
                    continue;
                }

                if (!ContainsOrdinalIgnoreCase(result, jid))
                {
                    result.Add(jid);
                }
            }

            return result;
        }

        public static string Format(IReadOnlyList<string> jids)
        {
            if (jids == null || jids.Count == 0)
            {
                return string.Empty;
            }

            var parts = new List<string>(jids.Count);
            for (int i = 0; i < jids.Count; i++)
            {
                string jid = jids[i];
                if (string.IsNullOrWhiteSpace(jid))
                {
                    continue;
                }

                string trimmed = jid.Trim();
                if (!ContainsOrdinalIgnoreCase(parts, trimmed))
                {
                    parts.Add(trimmed);
                }
            }

            return parts.Count == 0 ? string.Empty : string.Join("|", parts);
        }

        /// <summary>
        /// Adds <paramref name="canonicalJid"/> at the end (MRU). When over cap, drops the
        /// oldest entries from the front. Returns true when the stored set changed.
        /// </summary>
        public static bool TryAdd(List<string> jids, string canonicalJid, int cap)
        {
            if (jids == null || string.IsNullOrWhiteSpace(canonicalJid))
            {
                return false;
            }

            string key = canonicalJid.Trim();
            int limit = cap < 1 ? Cap : cap;

            for (int i = 0; i < jids.Count; i++)
            {
                if (string.Equals(jids[i], key, StringComparison.OrdinalIgnoreCase))
                {
                    if (i == jids.Count - 1)
                    {
                        return false;
                    }

                    jids.RemoveAt(i);
                    jids.Add(key);
                    TrimFront(jids, limit);
                    return true;
                }
            }

            jids.Add(key);
            TrimFront(jids, limit);
            return true;
        }

        /// <summary>Removes any JID matching the given keys (case-insensitive). True if any removed.</summary>
        public static bool RemoveAny(List<string> jids, IReadOnlyList<string> keys)
        {
            if (jids == null || jids.Count == 0 || keys == null || keys.Count == 0)
            {
                return false;
            }

            bool removed = false;
            for (int k = 0; k < keys.Count; k++)
            {
                string key = keys[k];
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                string trimmed = key.Trim();
                for (int i = jids.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(jids[i], trimmed, StringComparison.OrdinalIgnoreCase))
                    {
                        jids.RemoveAt(i);
                        removed = true;
                    }
                }
            }

            return removed;
        }

        private static void TrimFront(List<string> jids, int limit)
        {
            while (jids.Count > limit)
            {
                jids.RemoveAt(0);
            }
        }

        private static bool ContainsOrdinalIgnoreCase(List<string> list, string value)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
