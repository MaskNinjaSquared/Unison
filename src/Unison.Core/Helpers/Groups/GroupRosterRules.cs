using System;
using System.Collections.Generic;
using Unison.Core.Contracts;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// The decisions a group roster goes through on its way to and from the row: copying it for
    /// the store, rebuilding it from the store, matching a wire participant to the member it
    /// already was, and the person keys the groups-in-common lookup is written under.
    /// </summary>
    /// <remarks>
    /// One participant is reachable under a LID, a phone-number JID and a bare phone number, and
    /// the wire uses whichever it likes. Every rule here resolves through <see cref="IJidResolver"/>
    /// so a member that changed spelling between two replies is still the same member.
    /// </remarks>
    public static class GroupRosterRules
    {
        private const string PhoneServer = "@s.whatsapp.net";
        private const string GroupServer = "@g.us";

        /// <summary>
        /// Detached copies for a background write, so the store never reads rows the UI thread
        /// is still merging into. Null when nothing is worth writing.
        /// </summary>
        public static List<GroupMember> CopyForStore(IList<GroupMember> source)
        {
            if (source == null || source.Count == 0)
            {
                return null;
            }

            var copy = new List<GroupMember>(source.Count);
            foreach (var member in source)
            {
                if (member == null)
                {
                    continue;
                }

                copy.Add(Copy(member, member.DisplayName));
            }

            return copy.Count == 0 ? null : copy;
        }

        /// <summary>
        /// Rows read back from the store, with a name resolved for any member that was persisted
        /// before one was known. Never null.
        /// </summary>
        public static List<GroupMember> HydrateFromStore(
            IReadOnlyList<GroupMember> stored,
            Func<string, string> resolveDisplayName)
        {
            if (stored == null || stored.Count == 0)
            {
                return new List<GroupMember>();
            }

            var hydrated = new List<GroupMember>(stored.Count);
            for (int i = 0; i < stored.Count; i++)
            {
                GroupMember row = stored[i];
                if (row == null || string.IsNullOrWhiteSpace(row.Jid))
                {
                    continue;
                }

                string display = row.DisplayName;
                if (string.IsNullOrWhiteSpace(display))
                {
                    display = resolveDisplayName(row.Jid);
                }

                hydrated.Add(Copy(row, display));
            }

            return hydrated;
        }

        /// <summary>
        /// Puts a restored roster on the row. UI thread only.
        /// </summary>
        /// <returns>
        /// False when the row already holds members: the store read runs off the UI thread, and a
        /// live metadata reply that landed meanwhile is fresher than anything on disk.
        /// </returns>
        public static bool ApplyRestored(ChatItem chat, List<GroupMember> hydrated)
        {
            if (chat == null || hydrated == null || hydrated.Count == 0 || chat.HasGroupMembers)
            {
                return false;
            }

            chat.GroupMembers = hydrated;
            if (chat.GroupMemberCount < hydrated.Count)
            {
                chat.GroupMemberCount = hydrated.Count;
            }

            if (!chat.IsGroup)
            {
                chat.IsGroup = true;
            }

            return true;
        }

        /// <summary>
        /// Every person JID a member's group membership is written under — the JID, the LID, the
        /// phone number, and the canonical form of each — so groups-in-common answers whichever
        /// one the UI happens to hold.
        /// </summary>
        /// <param name="jids">
        /// Null when the caller has no resolver: only the addresses the member already carries
        /// are returned, with no canonical half.
        /// </param>
        public static IReadOnlyList<string> MembershipPersonKeys(GroupMember member, IJidResolver jids)
        {
            var keys = new List<string>();
            if (member == null)
            {
                return keys;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Consider(string raw)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return;
                }

                string key = JidHelper.Normalize(raw);
                if (string.IsNullOrEmpty(key) || IsGroup(key))
                {
                    return;
                }

                if (seen.Add(key))
                {
                    keys.Add(key);
                }
            }

            Consider(member.Jid);
            if (jids != null)
            {
                Consider(jids.GetCanonicalJid(member.Jid));
            }

            Consider(member.Lid);
            if (jids != null && !string.IsNullOrWhiteSpace(member.Lid))
            {
                Consider(jids.GetCanonicalJid(member.Lid));
            }

            string phone = member.PhoneNumber;
            if (!string.IsNullOrWhiteSpace(phone))
            {
                if (phone.IndexOf('@') >= 0)
                {
                    Consider(phone);
                }
                else
                {
                    string digits = PhoneNumberHelper.NormalizePhoneDigits(phone);
                    if (!string.IsNullOrEmpty(digits))
                    {
                        Consider(digits + PhoneServer);
                    }
                }
            }

            return keys;
        }

        /// <summary>
        /// The membership rows for a whole roster, one per person key, each pair written once.
        /// The first member to claim a key decides its role.
        /// </summary>
        public static List<PersonGroupMembership> Memberships(
            string groupJid,
            IEnumerable<GroupMember> members,
            IJidResolver jids)
        {
            var rows = new List<PersonGroupMembership>();
            if (string.IsNullOrWhiteSpace(groupJid) || members == null)
            {
                return rows;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var member in members)
            {
                if (member == null)
                {
                    continue;
                }

                foreach (string personJid in MembershipPersonKeys(member, jids))
                {
                    if (!seen.Add(personJid + "\u001f" + groupJid))
                    {
                        continue;
                    }

                    rows.Add(new PersonGroupMembership
                    {
                        PersonJid = personJid,
                        GroupJid = groupJid,
                        Role = member.Role
                    });
                }
            }

            return rows;
        }

        /// <summary>Whether any of the member's addresses resolves to <paramref name="canonical"/>.</summary>
        /// <param name="jids">
        /// Null compares the normalized addresses only, which is what a caller without a resolver can know.
        /// </param>
        public static bool MemberMatches(GroupMember member, string canonical, IJidResolver jids)
        {
            if (member == null || string.IsNullOrWhiteSpace(canonical))
            {
                return false;
            }

            return MatchesCanonical(member.Jid, canonical, jids) ||
                   MatchesCanonical(member.PhoneNumber, canonical, jids) ||
                   MatchesCanonical(member.Lid, canonical, jids);
        }

        /// <summary>
        /// The member a wire participant already was, so its avatar and lookup history carry over.
        /// Exact JID first; otherwise any of the participant's addresses against any of the member's.
        /// </summary>
        public static GroupMember FindPrevious(
            IDictionary<string, GroupMember> previousByJid,
            IList<GroupMember> previous,
            string jid,
            string lid,
            string phoneNumber,
            IJidResolver jids)
        {
            GroupMember prior;
            if (previousByJid != null && jid != null && previousByJid.TryGetValue(jid, out prior))
            {
                return prior;
            }

            if (previous == null || previous.Count == 0)
            {
                return null;
            }

            string canonical = jids.GetCanonicalJid(jid);
            for (int i = 0; i < previous.Count; i++)
            {
                GroupMember existing = previous[i];
                if (MemberMatches(existing, canonical, jids))
                {
                    return existing;
                }

                if (!string.IsNullOrWhiteSpace(lid) &&
                    MemberMatches(existing, jids.GetCanonicalJid(lid), jids))
                {
                    return existing;
                }

                if (!string.IsNullOrWhiteSpace(phoneNumber) &&
                    MemberMatches(existing, jids.GetCanonicalJid(JidHelper.Normalize(
                        phoneNumber.IndexOf('@') >= 0 ? phoneNumber : phoneNumber + PhoneServer)), jids))
                {
                    return existing;
                }
            }

            return null;
        }

        /// <summary>
        /// True when both sides hold the same set of member JIDs, which lets the caller merge
        /// fields onto the existing instances instead of replacing the collection and forcing
        /// the members list to relayout.
        /// </summary>
        public static bool RosterJidSetsEqual(
            IDictionary<string, GroupMember> previousByJid,
            IList<GroupMember> next)
        {
            if (previousByJid == null || next == null || previousByJid.Count != next.Count)
            {
                return false;
            }

            for (int i = 0; i < next.Count; i++)
            {
                GroupMember member = next[i];
                if (member == null ||
                    string.IsNullOrWhiteSpace(member.Jid) ||
                    !previousByJid.ContainsKey(member.Jid))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Copies incoming fields onto the matching existing members. Blank incoming values are
        /// skipped rather than written: a metadata response that omits a phone number is not
        /// telling us the member lost one.
        /// </summary>
        public static void MergeInPlace(IList<GroupMember> existing, IList<GroupMember> incoming)
        {
            if (existing == null || incoming == null)
            {
                return;
            }

            var byJid = new Dictionary<string, GroupMember>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < incoming.Count; i++)
            {
                GroupMember src = incoming[i];
                if (src == null || string.IsNullOrWhiteSpace(src.Jid))
                {
                    continue;
                }

                byJid[src.Jid] = src;
            }

            for (int i = 0; i < existing.Count; i++)
            {
                GroupMember dest = existing[i];
                if (dest == null || string.IsNullOrWhiteSpace(dest.Jid))
                {
                    continue;
                }

                GroupMember src;
                if (!byJid.TryGetValue(dest.Jid, out src) || src == null)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(src.PhoneNumber))
                {
                    dest.PhoneNumber = src.PhoneNumber;
                }

                if (!string.IsNullOrWhiteSpace(src.Lid))
                {
                    dest.Lid = src.Lid;
                }

                dest.Role = src.Role;

                if (!string.IsNullOrWhiteSpace(src.DisplayName))
                {
                    dest.DisplayName = src.DisplayName;
                }

                if (!string.IsNullOrWhiteSpace(src.AvatarUrl))
                {
                    dest.AvatarUrl = src.AvatarUrl;
                }

                if (src.AvatarFetchedAtUtc.HasValue)
                {
                    dest.AvatarFetchedAtUtc = src.AvatarFetchedAtUtc;
                }

                if (src.AvatarFetchFailedAtUtc.HasValue)
                {
                    dest.AvatarFetchFailedAtUtc = src.AvatarFetchFailedAtUtc;
                }

                if (!string.IsNullOrWhiteSpace(src.AvatarFetchFailureReason))
                {
                    dest.AvatarFetchFailureReason = src.AvatarFetchFailureReason;
                }
            }
        }

        private static bool MatchesCanonical(string jid, string canonical, IJidResolver jids)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return false;
            }

            string normalized = JidHelper.Normalize(jid);
            string other = jids != null ? jids.GetCanonicalJid(normalized) : normalized;
            if (string.IsNullOrWhiteSpace(other))
            {
                other = normalized;
            }

            return string.Equals(other, canonical, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsGroup(string normalizedJid) =>
            normalizedJid.EndsWith(GroupServer, StringComparison.OrdinalIgnoreCase);

        private static GroupMember Copy(GroupMember source, string displayName)
        {
            return new GroupMember
            {
                Jid = source.Jid,
                PhoneNumber = source.PhoneNumber,
                Lid = source.Lid,
                DisplayName = displayName,
                Role = source.Role,
                AvatarUrl = source.AvatarUrl,
                AvatarFetchedAtUtc = source.AvatarFetchedAtUtc,
                AvatarFetchFailedAtUtc = source.AvatarFetchFailedAtUtc,
                AvatarFetchFailureReason = source.AvatarFetchFailureReason
            };
        }
    }
}
