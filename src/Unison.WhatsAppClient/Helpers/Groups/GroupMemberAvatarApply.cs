// =============================================================================
// GroupMemberAvatarApply — stamp a member picture outcome onto roster rows.
// Must not walk every member of every group with MemberMatches (that froze
// Mobile for tens of seconds inside GroupRosterPolicy.RunLowAsync).
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Helpers
{
    public static class GroupMemberAvatarApply
    {
        /// <summary>
        /// Updates matching roster members. When <paramref name="limitToGroupJid"/> is set, only
        /// that group is touched (open / hydrate path). Otherwise every group row is probed via
        /// <see cref="ChatItem.FindGroupMember"/> (O(groups), not O(groups×members)).
        /// </summary>
        /// <returns>How many member rows were stamped.</returns>
        public static int Apply(
            IEnumerable<ChatItem> chats,
            string memberJid,
            GroupMemberAvatarFetchResult result,
            IJidResolver jids,
            string limitToGroupJid = null,
            DateTime? nowUtc = null)
        {
            if (chats == null || result == null || string.IsNullOrWhiteSpace(memberJid))
            {
                return 0;
            }

            DateTime stampUtc = nowUtc ?? DateTime.UtcNow;
            string normalized = JidHelper.Normalize(memberJid) ?? memberJid;
            string canonical = jids != null
                ? jids.GetCanonicalJid(normalized)
                : normalized;
            if (string.IsNullOrWhiteSpace(canonical))
            {
                canonical = normalized;
            }

            string alias = null;
            if (jids != null)
            {
                jids.TryGetAlias(canonical, out alias);
                if (string.IsNullOrWhiteSpace(alias))
                {
                    jids.TryGetAlias(normalized, out alias);
                }
            }

            string limitKey = null;
            if (!string.IsNullOrWhiteSpace(limitToGroupJid))
            {
                limitKey = jids != null
                    ? jids.GetCanonicalJid(limitToGroupJid)
                    : JidHelper.Normalize(limitToGroupJid);
                if (string.IsNullOrWhiteSpace(limitKey))
                {
                    limitKey = JidHelper.Normalize(limitToGroupJid) ?? limitToGroupJid;
                }
            }

            int stamped = 0;
            foreach (ChatItem chat in chats)
            {
                if (chat == null || !chat.IsGroup ||
                    chat.GroupMembers == null || chat.GroupMembers.Count == 0)
                {
                    continue;
                }

                if (limitKey != null && !GroupMatches(chat, limitKey, limitToGroupJid, jids))
                {
                    continue;
                }

                GroupMember member = FindMember(chat, canonical, normalized, alias);
                if (member == null)
                {
                    continue;
                }

                Stamp(member, result, stampUtc);
                stamped++;

                if (limitKey != null)
                {
                    break;
                }
            }

            return stamped;
        }

        private static bool GroupMatches(
            ChatItem chat,
            string limitKey,
            string limitRaw,
            IJidResolver jids)
        {
            string raw = JidHelper.Normalize(chat.JID) ?? chat.JID;
            if (string.Equals(raw, limitKey, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(raw, limitRaw, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (jids == null)
            {
                return false;
            }

            string chatCanonical = jids.GetCanonicalJid(chat.JID);
            return !string.IsNullOrWhiteSpace(chatCanonical) &&
                   string.Equals(chatCanonical, limitKey, StringComparison.OrdinalIgnoreCase);
        }

        private static GroupMember FindMember(
            ChatItem chat,
            string canonical,
            string normalized,
            string alias)
        {
            GroupMember member = chat.FindGroupMember(canonical);
            if (member != null)
            {
                return member;
            }

            if (!string.Equals(normalized, canonical, StringComparison.OrdinalIgnoreCase))
            {
                member = chat.FindGroupMember(normalized);
                if (member != null)
                {
                    return member;
                }
            }

            if (!string.IsNullOrWhiteSpace(alias))
            {
                member = chat.FindGroupMember(alias);
            }

            return member;
        }

        private static void Stamp(
            GroupMember member,
            GroupMemberAvatarFetchResult result,
            DateTime nowUtc)
        {
            if (result.HasPicture)
            {
                ChatAvatarOutcome.RecordCached(member, result.LocalUri, nowUtc);
            }
            else if (result.IsNotFound)
            {
                ChatAvatarOutcome.RecordAbsent(member, "no-picture", nowUtc);
            }
            else
            {
                ChatAvatarOutcome.RecordFailure(
                    member,
                    result.FailureReason ?? "fetch-failed",
                    nowUtc);
            }
        }
    }
}
