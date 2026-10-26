// =============================================================================
// ChatAuthorStripMaps — one-shot indexes for the list group-author strip.
// Same idea as GroupParticipantLookup for open-chat bubbles: build once per pass,
// then ResolveDisplayName stays O(1) instead of re-walking Chats / GroupMembers.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Contracts;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public static class ChatAuthorStripMaps
    {
        /// <summary>
        /// 1:1 chat JID (raw + canonical) → display name. Skips groups and empty/fallback labels.
        /// </summary>
        public static Dictionary<string, string> BuildDirectChatNames(
            IEnumerable<ChatItem> chats,
            IJidResolver jids)
        {
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (chats == null)
            {
                return names;
            }

            foreach (ChatItem chat in chats)
            {
                if (chat == null || chat.IsGroup || string.IsNullOrWhiteSpace(chat.JID))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(chat.Name) ||
                    chat.Name.IndexOf('@') >= 0 ||
                    SelfChatNaming.IsKnownFallback(chat.Name))
                {
                    continue;
                }

                string label = chat.Name.Trim();
                string raw = JidHelper.Normalize(chat.JID) ?? chat.JID;
                IndexName(names, raw, label);

                string canonical = jids != null
                    ? jids.GetCanonicalJid(chat.JID)
                    : raw;
                if (!string.IsNullOrWhiteSpace(canonical))
                {
                    IndexName(names, canonical, label);
                }
            }

            return names;
        }

        /// <summary>
        /// Roster member keyed by Jid / Lid / phone / canonical — tip lookup without a linear scan.
        /// </summary>
        public static Dictionary<string, GroupMember> BuildRosterByCanonical(
            ChatItem group,
            IJidResolver jids)
        {
            var roster = new Dictionary<string, GroupMember>(StringComparer.OrdinalIgnoreCase);
            if (group?.GroupMembers == null || group.GroupMembers.Count == 0)
            {
                return roster;
            }

            for (int i = 0; i < group.GroupMembers.Count; i++)
            {
                GroupMember member = group.GroupMembers[i];
                if (member == null)
                {
                    continue;
                }

                IndexRosterKey(roster, member.Jid, member, jids);
                IndexRosterKey(roster, member.Lid, member, jids);
                IndexRosterKey(roster, member.PhoneNumber, member, jids);
            }

            return roster;
        }

        /// <summary>
        /// One tip sender in the roster — cheaper than indexing every member when the strip
        /// only needs <paramref name="participantJid"/>.
        /// </summary>
        public static GroupMember FindRosterTip(
            ChatItem group,
            string participantJid,
            IJidResolver jids)
        {
            if (group?.GroupMembers == null ||
                group.GroupMembers.Count == 0 ||
                string.IsNullOrWhiteSpace(participantJid))
            {
                return null;
            }

            string tip = JidHelper.Normalize(participantJid) ?? participantJid;
            string tipCanonical = jids != null
                ? jids.GetCanonicalJid(participantJid)
                : tip;

            for (int i = 0; i < group.GroupMembers.Count; i++)
            {
                GroupMember member = group.GroupMembers[i];
                if (member == null)
                {
                    continue;
                }

                if (KeyMatches(member.Jid, tip, tipCanonical, jids) ||
                    KeyMatches(member.Lid, tip, tipCanonical, jids) ||
                    KeyMatches(member.PhoneNumber, tip, tipCanonical, jids))
                {
                    return member;
                }
            }

            return null;
        }

        /// <summary>O(1) 1:1 name lookup under tip or canonical key.</summary>
        public static bool TryGetDirectName(
            IReadOnlyDictionary<string, string> directChatNames,
            string participantJid,
            IJidResolver jids,
            out string name)
        {
            name = null;
            if (directChatNames == null || string.IsNullOrWhiteSpace(participantJid))
            {
                return false;
            }

            string tip = JidHelper.Normalize(participantJid) ?? participantJid;
            if (directChatNames.TryGetValue(tip, out name) && !string.IsNullOrWhiteSpace(name))
            {
                return true;
            }

            string canonical = jids != null
                ? jids.GetCanonicalJid(participantJid)
                : tip;
            if (!string.IsNullOrWhiteSpace(canonical) &&
                directChatNames.TryGetValue(canonical, out name) &&
                !string.IsNullOrWhiteSpace(name))
            {
                return true;
            }

            name = null;
            return false;
        }

        private static bool KeyMatches(
            string key,
            string tip,
            string tipCanonical,
            IJidResolver jids)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            string normalized = JidHelper.Normalize(key) ?? key;
            if (string.Equals(normalized, tip, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(tipCanonical) &&
                 string.Equals(normalized, tipCanonical, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (jids == null)
            {
                return false;
            }

            string canonical = jids.GetCanonicalJid(key);
            return !string.IsNullOrWhiteSpace(canonical) &&
                   (string.Equals(canonical, tip, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(canonical, tipCanonical, StringComparison.OrdinalIgnoreCase));
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

        private static void IndexRosterKey(
            Dictionary<string, GroupMember> roster,
            string key,
            GroupMember member,
            IJidResolver jids)
        {
            if (roster == null || member == null || string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            string normalized = JidHelper.Normalize(key) ?? key;
            if (!roster.ContainsKey(normalized))
            {
                roster[normalized] = member;
            }

            string canonical = jids != null
                ? jids.GetCanonicalJid(key)
                : normalized;
            if (!string.IsNullOrWhiteSpace(canonical) && !roster.ContainsKey(canonical))
            {
                roster[canonical] = member;
            }
        }
    }
}
