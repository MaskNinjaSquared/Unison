// =============================================================================
// ChatAuthorSweepSelection — pending-chat fast path for the group author strip.
// RefreshChat must not walk the whole catalogue or BuildDirectChatNames(all).
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Contracts;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public static class ChatAuthorSweepSelection
    {
        /// <summary>
        /// RefreshChat / pending groups only — FindChat, never foreach the catalogue.
        /// </summary>
        public static bool CanUsePendingChatFastPath(
            bool unnamedPass,
            int pendingChatCount,
            int pendingParticipantCount)
        {
            return !unnamedPass &&
                pendingParticipantCount == 0 &&
                pendingChatCount > 0;
        }

        public static List<ChatItem> CollectPendingChats(
            HashSet<string> pendingChats,
            Func<string, ChatItem> findChat,
            IJidResolver jids)
        {
            var work = new List<ChatItem>();
            if (pendingChats == null || pendingChats.Count == 0 || findChat == null)
            {
                return work;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in pendingChats)
            {
                ChatItem chat = findChat(key);
                if (chat == null || !chat.IsGroup || chat.LastMessageIsFromMe)
                {
                    continue;
                }

                string id = JidHelper.Normalize(chat.JID) ?? chat.JID;
                if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
                {
                    continue;
                }

                string canonical = jids != null ? jids.PeekCanonicalJid(chat.JID) : null;
                if (!string.IsNullOrWhiteSpace(canonical))
                {
                    seen.Add(canonical);
                }

                work.Add(chat);
            }

            return work;
        }

        /// <summary>
        /// 1:1 names only for tip senders on <paramref name="work"/> — O(tips), not O(catalogue).
        /// </summary>
        public static Dictionary<string, string> BuildDirectNamesForWork(
            IReadOnlyList<ChatItem> work,
            Func<string, ChatItem> findChat,
            IJidResolver jids)
        {
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (work == null || work.Count == 0 || findChat == null)
            {
                return names;
            }

            for (int i = 0; i < work.Count; i++)
            {
                string tip = work[i]?.LastMessageParticipantJid;
                if (string.IsNullOrWhiteSpace(tip))
                {
                    continue;
                }

                IndexDirectIfPresent(names, findChat, tip, jids);

                string canonical = jids != null ? jids.PeekCanonicalJid(tip) : null;
                if (!string.IsNullOrWhiteSpace(canonical) &&
                    !string.Equals(canonical, tip, StringComparison.OrdinalIgnoreCase))
                {
                    IndexDirectIfPresent(names, findChat, canonical, jids);
                }

                string alias;
                if (jids != null &&
                    jids.TryGetAlias(tip, out alias) &&
                    !string.IsNullOrWhiteSpace(alias))
                {
                    IndexDirectIfPresent(names, findChat, alias, jids);
                }
            }

            return names;
        }

        private static void IndexDirectIfPresent(
            Dictionary<string, string> names,
            Func<string, ChatItem> findChat,
            string jid,
            IJidResolver jids)
        {
            ChatItem chat = findChat(jid);
            if (chat == null || chat.IsGroup || string.IsNullOrWhiteSpace(chat.JID))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(chat.Name) ||
                chat.Name.IndexOf('@') >= 0 ||
                SelfChatNaming.IsKnownFallback(chat.Name))
            {
                return;
            }

            string label = chat.Name.Trim();
            string raw = JidHelper.Normalize(chat.JID) ?? chat.JID;
            IndexName(names, raw, label);

            string canonical = jids != null ? jids.PeekCanonicalJid(chat.JID) : raw;
            if (!string.IsNullOrWhiteSpace(canonical))
            {
                IndexName(names, canonical, label);
            }
        }

        private static void IndexName(Dictionary<string, string> map, string key, string value)
        {
            if (map == null || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (!map.ContainsKey(key))
            {
                map[key] = value;
            }
        }
    }
}
