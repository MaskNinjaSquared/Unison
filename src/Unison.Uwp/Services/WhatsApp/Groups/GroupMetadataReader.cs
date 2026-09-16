// =============================================================================
// GroupMetadataReader
//
// Turns a w:g2 IQ response into plain objects. Reading the wire and writing the
// chat list were the same code, which meant the parser could only be reached
// through the UI thread and could only be checked by watching rows change.
//
// This half is pure: give it a BinaryNode, get back a subject, a role, a member
// list. No socket, no chat state, no dispatcher. The applying half stays with
// whoever owns the rows, because that is a thread-affinity question, not a
// protocol one.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using Unison.Baileys.Protocol;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Uwp.Client;
using Unison.Uwp.Helpers;

namespace Unison.Uwp.Services.WhatsApp.Groups
{
    /// <summary>What a group listing says about one group, read off the wire.</summary>
    internal sealed class GroupListingEntry
    {
        public string Jid;
        public string Subject;
        public bool AnnounceOnly;
        public GroupParticipantRole MyRole;
        public int MemberCount;
        public List<GroupMemberDraft> Members;
    }

    /// <summary>A participant as the wire describes it, before names and avatars are attached.</summary>
    internal sealed class GroupMemberDraft
    {
        public string Jid;
        public string PhoneNumber;
        public string Lid;
        public GroupParticipantRole Role;
    }

    internal sealed class GroupMetadataReader
    {
        /// <summary>
        /// Large groups answer with thousands of participants. Rosters are for showing members
        /// and resolving mentions, and neither needs the tail of a five-thousand-member list.
        /// </summary>
        private const int MaxMembers = 512;

        private readonly Func<string, string> _canonical;
        private readonly Func<string, bool> _isSelfLinked;

        /// <summary>
        /// Identity arrives as two functions rather than <see cref="IJidResolver"/> because the
        /// PN/LID alias table is still inside the client until phase 3.7, and recognising the
        /// logged-in account reads it: a participant row can carry a device-suffixed PN whose
        /// base LID is what the account is actually known by. Reimplementing that here to avoid
        /// the seam would mean guessing at the role the composer trusts. At 3.7 both collapse
        /// into a single resolver.
        /// </summary>
        internal GroupMetadataReader(Func<string, string> canonical, Func<string, bool> isSelfLinked)
        {
            _canonical = canonical ?? throw new ArgumentNullException(nameof(canonical));
            _isSelfLinked = isSelfLinked ?? throw new ArgumentNullException(nameof(isSelfLinked));
        }

        /// <summary>
        /// Every group in a participating-groups listing, keyed by canonical JID. Reading all of
        /// them before anything is applied is deliberate: the listing answers for the whole
        /// account at once, and applying group by group meant one hop to the UI thread and one
        /// walk of the chat list each.
        /// </summary>
        public Dictionary<string, GroupListingEntry> ReadListing(List<BinaryNode> groupNodes)
        {
            var parsed = new Dictionary<string, GroupListingEntry>(StringComparer.OrdinalIgnoreCase);
            if (groupNodes == null)
            {
                return parsed;
            }

            foreach (var node in groupNodes)
            {
                string id;
                if (node?.Attrs == null ||
                    !node.Attrs.TryGetValue("id", out id) ||
                    string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                string jid = id.Contains("@") ? id : id + "@g.us";
                string subject;
                node.Attrs.TryGetValue("subject", out subject);

                parsed[_canonical(JidHelper.Normalize(jid))] = new GroupListingEntry
                {
                    Jid = jid,
                    Subject = subject,
                    AnnounceOnly = IsAnnounceOnly(node),
                    MyRole = ResolveMyRole(node),
                    MemberCount = CountMembers(node),
                    Members = ReadMemberDrafts(node)
                };
            }

            return parsed;
        }

        /// <summary>
        /// The group node for this JID inside a metadata response. A node with no id is accepted
        /// because single-group queries answer without echoing it back.
        /// </summary>
        public BinaryNode FindGroupNode(BinaryNode response, string groupJid)
        {
            if (response == null)
            {
                return null;
            }

            string target = JidHelper.Normalize(groupJid);
            foreach (var group in response.FindAllDescendants("group"))
            {
                if (group?.Attrs == null)
                {
                    continue;
                }

                string id;
                group.Attrs.TryGetValue("id", out id);
                string normalizedId = NormalizeGroupJid(id);
                if (string.IsNullOrWhiteSpace(normalizedId) ||
                    string.Equals(normalizedId, target, StringComparison.OrdinalIgnoreCase))
                {
                    return group;
                }
            }

            // A response whose group nodes are nested somewhere the descendant walk did not reach
            // still usually has one directly under the root.
            return response.GetChild("group");
        }

        public string ExtractSubject(BinaryNode response, string groupJid)
        {
            if (response == null)
            {
                return null;
            }

            foreach (var group in response.FindAllDescendants("group"))
            {
                if (group?.Attrs == null)
                {
                    continue;
                }

                string id;
                string subject;
                group.Attrs.TryGetValue("id", out id);
                group.Attrs.TryGetValue("subject", out subject);
                if (string.IsNullOrWhiteSpace(subject))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(id) ||
                    string.Equals(
                        JidHelper.Normalize(id),
                        JidHelper.Normalize(groupJid),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return subject;
                }
            }

            var direct = response.GetChild("group");
            string directSubject;
            if (direct?.Attrs != null &&
                direct.Attrs.TryGetValue("subject", out directSubject) &&
                !string.IsNullOrWhiteSpace(directSubject))
            {
                return directSubject;
            }

            return null;
        }

        public bool IsAnnounceOnly(BinaryNode groupNode)
        {
            return groupNode?.GetChild("announcement") != null;
        }

        /// <summary>
        /// Falls back to the listed participants when the <c>size</c> attribute is missing or
        /// smaller — a truncated participant list still tells us more than a stale count.
        /// </summary>
        public int CountMembers(BinaryNode groupNode)
        {
            if (groupNode == null)
            {
                return 0;
            }

            int listed = 0;
            List<BinaryNode> participants = groupNode.GetChildren("participant");
            if (participants != null)
            {
                listed = participants.Count;
            }

            int size;
            if (int.TryParse(groupNode.GetAttribute("size"), out size) && size > listed)
            {
                return size;
            }

            return listed;
        }

        public List<GroupMemberDraft> ReadMemberDrafts(BinaryNode groupNode)
        {
            var drafts = new List<GroupMemberDraft>();
            if (groupNode == null)
            {
                return drafts;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (BinaryNode participant in groupNode.GetChildren("participant"))
            {
                if (participant?.Attrs == null)
                {
                    continue;
                }

                string jid = participant.Attrs.GetDictionaryValueOrDefault("jid", string.Empty);
                if (string.IsNullOrWhiteSpace(jid))
                {
                    continue;
                }

                string canonical = JidHelper.Normalize(jid);
                if (string.IsNullOrWhiteSpace(canonical) || !seen.Add(canonical))
                {
                    continue;
                }

                string admin = participant.Attrs.GetDictionaryValueOrDefault("admin", string.Empty);
                if (string.IsNullOrWhiteSpace(admin))
                {
                    admin = participant.Attrs.GetDictionaryValueOrDefault("type", string.Empty);
                }

                drafts.Add(new GroupMemberDraft
                {
                    Jid = canonical,
                    PhoneNumber = participant.Attrs.GetDictionaryValueOrDefault("phone_number", string.Empty),
                    Lid = participant.Attrs.GetDictionaryValueOrDefault("lid", string.Empty),
                    Role = ParseRole(admin)
                });

                if (drafts.Count >= MaxMembers)
                {
                    break;
                }
            }

            return drafts;
        }

        /// <summary>
        /// The logged-in account's rank in this group. Matched against the PN, the LID and their
        /// aliases, because the participant row can carry any of the three.
        /// </summary>
        public GroupParticipantRole ResolveMyRole(BinaryNode groupNode)
        {
            if (groupNode == null)
            {
                return GroupParticipantRole.Member;
            }

            foreach (BinaryNode participant in groupNode.GetChildren("participant"))
            {
                if (participant?.Attrs == null)
                {
                    continue;
                }

                string jid = participant.Attrs.GetDictionaryValueOrDefault("jid", string.Empty);
                string phone = participant.Attrs.GetDictionaryValueOrDefault("phone_number", string.Empty);
                string lid = participant.Attrs.GetDictionaryValueOrDefault("lid", string.Empty);
                if (!_isSelfLinked(jid) && !_isSelfLinked(phone) && !_isSelfLinked(lid))
                {
                    continue;
                }

                return ParseRole(participant.Attrs.GetDictionaryValueOrDefault("admin", string.Empty));
            }

            return GroupParticipantRole.Member;
        }

        /// <summary>
        /// Picture lookup order for a member. PN first: LID picture IQs often answer 404 and used
        /// to burn the only attempt.
        /// </summary>
        public List<string> GetPictureCandidates(GroupMember member)
        {
            var candidates = new List<string>();
            if (member == null)
            {
                return candidates;
            }

            Action<string> add = value =>
            {
                string normalized = JidHelper.Normalize(value);
                if (!string.IsNullOrWhiteSpace(normalized) &&
                    !candidates.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                {
                    candidates.Add(normalized);
                }
            };

            add(member.PhoneNumber);
            add(_canonical(member.PhoneNumber));
            add(_canonical(member.Jid));
            add(member.Jid);
            add(member.Lid);
            add(_canonical(member.Lid));
            return candidates;
        }

        /// <summary>
        /// True when a group label is just the chat id: <c>120363…</c> or the legacy
        /// <c>phone-timestamp</c> user part. Those are placeholders, not subjects.
        /// </summary>
        public static bool IsIdPlaceholder(string label, string groupJid)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return true;
            }

            string trimmed = label.Trim();
            if (trimmed.Contains("@"))
            {
                return true;
            }

            string bare = (groupJid ?? string.Empty).Split('@')[0];
            if (!string.IsNullOrEmpty(bare) &&
                string.Equals(trimmed, bare, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (trimmed.All(char.IsDigit))
            {
                return true;
            }

            string labelDigits = DigitsOnly(trimmed);
            string jidDigits = DigitsOnly(bare);
            bool hasLetters = trimmed.Any(char.IsLetter);
            return !hasLetters &&
                   jidDigits.Length >= 7 &&
                   string.Equals(labelDigits, jidDigits, StringComparison.Ordinal);
        }

        /// <summary>
        /// True when both sides hold the same set of member JIDs, which lets the caller merge
        /// fields onto the existing instances instead of replacing the collection and forcing
        /// the members list to relayout.
        /// </summary>
        public static bool RosterJidSetsEqual(
            Dictionary<string, GroupMember> previousByJid,
            List<GroupMember> next)
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
        public static void MergeInPlace(List<GroupMember> existing, List<GroupMember> incoming)
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

        public static GroupParticipantRole ParseRole(string adminAttr)
        {
            if (string.IsNullOrWhiteSpace(adminAttr))
            {
                return GroupParticipantRole.Member;
            }

            if (string.Equals(adminAttr, "superadmin", StringComparison.OrdinalIgnoreCase))
            {
                return GroupParticipantRole.SuperAdmin;
            }

            if (string.Equals(adminAttr, "admin", StringComparison.OrdinalIgnoreCase))
            {
                return GroupParticipantRole.Admin;
            }

            return GroupParticipantRole.Member;
        }

        /// <summary>A bare numeric id or an <c>@g.us</c> JID; anything else is not a group.</summary>
        public string NormalizeGroupJid(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            string value = raw.Trim();
            if (value.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase))
            {
                return JidHelper.Normalize(value);
            }

            if (value.IndexOf('@') < 0 && value.All(char.IsDigit))
            {
                return JidHelper.Normalize(value + "@g.us");
            }

            return null;
        }

        private static string DigitsOnly(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return new string(value.Where(char.IsDigit).ToArray());
        }
    }
}
