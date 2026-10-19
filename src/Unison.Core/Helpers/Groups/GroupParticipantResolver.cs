using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Shared group-participant avatar + display-name resolution for timeline bubbles and member info.
    /// Roster → hint (bubble / quote name) → WhatsApp names → Person → 1:1 chat.
    /// </summary>
    public static class GroupParticipantResolver
    {
        /// <summary>
        /// Fills empty <see cref="GroupMember.AvatarUrl"/> / <see cref="GroupMember.DisplayName"/>.
        /// </summary>
        public static void EnrichMember(
            GroupMember member,
            string participantJid,
            ChatItem groupChat,
            ParticipantResolutionContext context,
            string nameHint = null)
        {
            if (member == null)
            {
                return;
            }

            string jid = FirstNonEmpty(participantJid, member.Jid, member.Lid, member.PhoneNumber);
            if (string.IsNullOrWhiteSpace(jid))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(member.Jid))
            {
                member.Jid = JidHelper.Normalize(jid);
            }

            if (string.IsNullOrWhiteSpace(member.AvatarUrl))
            {
                member.AvatarUrl = ResolveAvatar(jid, groupChat, context, member);
            }

            if (string.IsNullOrWhiteSpace(member.DisplayName))
            {
                member.DisplayName = ResolveDisplayName(jid, groupChat, context, nameHint, member);
            }
        }

        /// <summary>
        /// Local avatar URI: roster → 1:1 chat → Person cache.
        /// When <paramref name="directChatAvatars"/> is provided, the Chats walk is skipped.
        /// When <paramref name="rosterByCanonical"/> is provided, the roster is not scanned linearly.
        /// </summary>
        public static string ResolveAvatar(
            string participantJid,
            ChatItem groupChat,
            ParticipantResolutionContext context,
            GroupMember rosterMember = null,
            IReadOnlyDictionary<string, string> directChatAvatars = null,
            IReadOnlyDictionary<string, GroupMember> rosterByCanonical = null)
        {
            if (string.IsNullOrWhiteSpace(participantJid))
            {
                return null;
            }

            string canonical = CanonicalJid(context, participantJid);
            if (string.IsNullOrWhiteSpace(canonical))
            {
                return null;
            }

            if (rosterMember != null && !string.IsNullOrWhiteSpace(rosterMember.AvatarUrl))
            {
                return rosterMember.AvatarUrl;
            }

            string fromRoster = FindAvatarOnRoster(groupChat, canonical, context, rosterByCanonical);
            if (!string.IsNullOrWhiteSpace(fromRoster))
            {
                return fromRoster;
            }

            string fromDirect = FindAvatarOnDirectChat(canonical, context, directChatAvatars);
            if (!string.IsNullOrWhiteSpace(fromDirect))
            {
                return fromDirect;
            }

            return FindAvatarOnPerson(canonical, participantJid, context?.People);
        }

        /// <summary>
        /// Display label: hint (bubble / quote) → roster → protocol names → Person → 1:1 chat → JID user.
        /// When <paramref name="directChatNames"/> is provided, the Chats walk is skipped.
        /// When <paramref name="rosterByCanonical"/> is provided, the roster is not scanned linearly.
        /// </summary>
        public static string ResolveDisplayName(
            string participantJid,
            ChatItem groupChat,
            ParticipantResolutionContext context,
            string nameHint = null,
            GroupMember rosterMember = null,
            IReadOnlyDictionary<string, string> directChatNames = null,
            IReadOnlyDictionary<string, GroupMember> rosterByCanonical = null)
        {
            if (string.IsNullOrWhiteSpace(participantJid))
            {
                return UsableLabel(nameHint, null) ? nameHint.Trim() : string.Empty;
            }

            string canonical = CanonicalJid(context, participantJid) ?? JidHelper.Normalize(participantJid);

            if (UsableLabel(nameHint, canonical))
            {
                return nameHint.Trim();
            }

            if (rosterMember != null && UsableLabel(rosterMember.DisplayName, canonical))
            {
                return rosterMember.DisplayName.Trim();
            }

            GroupMember fromRoster = rosterMember
                ?? FindRosterMember(groupChat, canonical, context, rosterByCanonical);
            if (fromRoster != null && UsableLabel(fromRoster.DisplayName, canonical))
            {
                return fromRoster.DisplayName.Trim();
            }

            string fromService = ResolveServiceName(context, participantJid);
            if (UsableLabel(fromService, canonical))
            {
                return fromService.Trim();
            }

            if (!string.Equals(canonical, participantJid, StringComparison.OrdinalIgnoreCase))
            {
                fromService = ResolveServiceName(context, canonical);
                if (UsableLabel(fromService, canonical))
                {
                    return fromService.Trim();
                }
            }

            Person person = TryGetPerson(context?.People, canonical, participantJid);
            if (person != null && UsableLabel(person.Name, canonical))
            {
                return person.Name.Trim();
            }

            string fromChat = FindNameOnDirectChat(canonical, context, directChatNames);
            if (UsableLabel(fromChat, canonical))
            {
                return fromChat.Trim();
            }

            return ShortJidUser(canonical);
        }

        private static GroupMember FindRosterMember(
            ChatItem groupChat,
            string canonical,
            ParticipantResolutionContext context,
            IReadOnlyDictionary<string, GroupMember> rosterByCanonical = null)
        {
            if (string.IsNullOrWhiteSpace(canonical))
            {
                return null;
            }

            // Indexed open-chat lookup: O(1). Do not fall through to a linear scan.
            if (rosterByCanonical != null)
            {
                GroupMember indexed;
                if (rosterByCanonical.TryGetValue(canonical, out indexed))
                {
                    return indexed;
                }

                return null;
            }

            if (groupChat?.GroupMembers == null)
            {
                return null;
            }

            for (int i = 0; i < groupChat.GroupMembers.Count; i++)
            {
                GroupMember member = groupChat.GroupMembers[i];
                if (member == null)
                {
                    continue;
                }

                if (JidsMatch(context, member.Jid, canonical) ||
                    JidsMatch(context, member.PhoneNumber, canonical) ||
                    JidsMatch(context, member.Lid, canonical))
                {
                    return member;
                }
            }

            return null;
        }

        private static string FindAvatarOnRoster(
            ChatItem groupChat,
            string canonical,
            ParticipantResolutionContext context,
            IReadOnlyDictionary<string, GroupMember> rosterByCanonical = null)
        {
            GroupMember member = FindRosterMember(groupChat, canonical, context, rosterByCanonical);
            return string.IsNullOrWhiteSpace(member?.AvatarUrl) ? null : member.AvatarUrl;
        }

        private static string FindAvatarOnDirectChat(
            string canonical,
            ParticipantResolutionContext context,
            IReadOnlyDictionary<string, string> directChatAvatars)
        {
            if (string.IsNullOrWhiteSpace(canonical))
            {
                return null;
            }

            if (directChatAvatars != null)
            {
                string indexed;
                if (directChatAvatars.TryGetValue(canonical, out indexed) &&
                    !string.IsNullOrWhiteSpace(indexed))
                {
                    return indexed;
                }

                return null;
            }

            ObservableCollection<ChatItem> chats = context?.ChatState?.Chats;
            if (chats == null)
            {
                return null;
            }

            for (int i = 0; i < chats.Count; i++)
            {
                ChatItem chat = chats[i];
                if (chat == null || chat.IsGroup || string.IsNullOrWhiteSpace(chat.JID))
                {
                    continue;
                }

                if (!JidsMatch(context, chat.JID, canonical))
                {
                    continue;
                }

                string url = chat.GetAvatarUrl(preferHigh: false);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    return url;
                }
            }

            return null;
        }

        private static string FindAvatarOnPerson(string canonical, string participantJid, Contracts.IPersonStore personStore)
        {
            Person person = TryGetPerson(personStore, canonical, participantJid);
            return string.IsNullOrWhiteSpace(person?.AvatarUrl) ? null : person.AvatarUrl;
        }

        private static string FindNameOnDirectChat(
            string canonical,
            ParticipantResolutionContext context,
            IReadOnlyDictionary<string, string> directChatNames)
        {
            if (string.IsNullOrWhiteSpace(canonical))
            {
                return null;
            }

            if (directChatNames != null)
            {
                string indexed;
                if (directChatNames.TryGetValue(canonical, out indexed) &&
                    UsableLabel(indexed, canonical))
                {
                    return indexed;
                }

                return null;
            }

            ObservableCollection<ChatItem> chats = context?.ChatState?.Chats;
            if (chats == null)
            {
                return null;
            }

            for (int i = 0; i < chats.Count; i++)
            {
                ChatItem chat = chats[i];
                if (chat == null || chat.IsGroup || string.IsNullOrWhiteSpace(chat.JID))
                {
                    continue;
                }

                if (!JidsMatch(context, chat.JID, canonical))
                {
                    continue;
                }

                if (UsableLabel(chat.Name, canonical))
                {
                    return chat.Name;
                }
            }

            return null;
        }

        private static Person TryGetPerson(Contracts.IPersonStore personStore, string canonical, string participantJid)
        {
            if (personStore == null)
            {
                return null;
            }

            Person person = personStore.TryGetCached(canonical);
            if (person == null &&
                !string.Equals(canonical, participantJid, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(participantJid))
            {
                person = personStore.TryGetCached(participantJid);
            }

            return person;
        }

        private static string ResolveServiceName(ParticipantResolutionContext context, string jid)
        {
            return context?.Contacts?.ResolveDisplayName(jid, "sender");
        }

        private static string CanonicalJid(ParticipantResolutionContext context, string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return null;
            }

            string canonical = context?.Jids?.GetCanonicalJid(jid);
            if (string.IsNullOrWhiteSpace(canonical))
            {
                canonical = JidHelper.Normalize(jid);
            }

            return canonical;
        }

        private static bool JidsMatch(ParticipantResolutionContext context, string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            string leftCanon = CanonicalJid(context, left);
            string rightCanon = CanonicalJid(context, right);
            return string.Equals(leftCanon, rightCanon, StringComparison.OrdinalIgnoreCase);
        }

        private static bool UsableLabel(string candidate, string jid)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return false;
            }

            string trimmed = candidate.Trim();
            if (trimmed.IndexOf('@') >= 0)
            {
                return false;
            }

            if (SelfChatNaming.IsKnownFallback(trimmed))
            {
                return false;
            }

            // Phone / LID user parts are placeholders, not display names. Digit-only labels
            // must not win for a LID participant whose PN bare is a different digit string.
            if (IsPhoneLikePlaceholder(trimmed))
            {
                return false;
            }

            string bare = ShortJidUser(jid);
            if (!string.IsNullOrEmpty(bare) &&
                string.Equals(trimmed, bare, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string phone = JidHelper.TryPhoneFromJid(jid);
            if (!string.IsNullOrEmpty(phone) &&
                string.Equals(trimmed, phone, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// True when <paramref name="candidate"/> is a real display label for
        /// <paramref name="participantJid"/> (not empty, not a raw JID/user/phone part).
        /// </summary>
        public static bool IsUsableDisplayLabel(string candidate, string participantJid)
        {
            return UsableLabel(candidate, participantJid);
        }

        /// <summary>
        /// Long digit-only strings are almost always a phone (or LID user) echoed as a "name".
        /// </summary>
        private static bool IsPhoneLikePlaceholder(string trimmed)
        {
            if (string.IsNullOrEmpty(trimmed) || trimmed.Length < 7)
            {
                return false;
            }

            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                if (c == '+' || c == ' ' || c == '-' || c == '(' || c == ')')
                {
                    continue;
                }

                if (!char.IsDigit(c))
                {
                    return false;
                }
            }

            return true;
        }

        private static string ShortJidUser(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return string.Empty;
            }

            string user = jid.Trim();
            int at = user.IndexOf('@');
            if (at > 0)
            {
                user = user.Substring(0, at);
            }

            int colon = user.IndexOf(':');
            if (colon > 0)
            {
                user = user.Substring(0, colon);
            }

            return user.Trim();
        }

        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null)
            {
                return null;
            }

            for (int i = 0; i < values.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(values[i]))
                {
                    return values[i];
                }
            }

            return null;
        }
    }
}
