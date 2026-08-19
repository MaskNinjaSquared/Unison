using System;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
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
            IWhatsAppService whatsApp,
            IPersonStore personStore,
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
                member.AvatarUrl = ResolveAvatar(jid, groupChat, whatsApp, personStore, member);
            }

            if (string.IsNullOrWhiteSpace(member.DisplayName))
            {
                member.DisplayName = ResolveDisplayName(jid, groupChat, whatsApp, personStore, nameHint, member);
            }
        }

        /// <summary>
        /// Local avatar URI: roster → 1:1 chat → Person cache.
        /// </summary>
        public static string ResolveAvatar(
            string participantJid,
            ChatItem groupChat,
            IWhatsAppService whatsApp,
            IPersonStore personStore,
            GroupMember rosterMember = null)
        {
            if (string.IsNullOrWhiteSpace(participantJid))
            {
                return null;
            }

            string canonical = CanonicalJid(whatsApp, participantJid);
            if (string.IsNullOrWhiteSpace(canonical))
            {
                return null;
            }

            if (rosterMember != null && !string.IsNullOrWhiteSpace(rosterMember.AvatarUrl))
            {
                return rosterMember.AvatarUrl;
            }

            string fromRoster = FindAvatarOnRoster(groupChat, canonical, whatsApp);
            if (!string.IsNullOrWhiteSpace(fromRoster))
            {
                return fromRoster;
            }

            string fromDirect = FindAvatarOnDirectChat(canonical, whatsApp);
            if (!string.IsNullOrWhiteSpace(fromDirect))
            {
                return fromDirect;
            }

            return FindAvatarOnPerson(canonical, participantJid, personStore);
        }

        /// <summary>
        /// Display label: hint (bubble / quote) → roster → protocol names → Person → 1:1 chat → JID user.
        /// </summary>
        public static string ResolveDisplayName(
            string participantJid,
            ChatItem groupChat,
            IWhatsAppService whatsApp,
            IPersonStore personStore,
            string nameHint = null,
            GroupMember rosterMember = null)
        {
            if (string.IsNullOrWhiteSpace(participantJid))
            {
                return UsableLabel(nameHint, null) ? nameHint.Trim() : string.Empty;
            }

            string canonical = CanonicalJid(whatsApp, participantJid) ?? JidHelper.Normalize(participantJid);

            if (UsableLabel(nameHint, canonical))
            {
                return nameHint.Trim();
            }

            if (rosterMember != null && UsableLabel(rosterMember.DisplayName, canonical))
            {
                return rosterMember.DisplayName.Trim();
            }

            GroupMember fromRoster = rosterMember ?? FindRosterMember(groupChat, canonical, whatsApp);
            if (fromRoster != null && UsableLabel(fromRoster.DisplayName, canonical))
            {
                return fromRoster.DisplayName.Trim();
            }

            string fromService = ResolveServiceName(whatsApp, participantJid);
            if (UsableLabel(fromService, canonical))
            {
                return fromService.Trim();
            }

            if (!string.Equals(canonical, participantJid, StringComparison.OrdinalIgnoreCase))
            {
                fromService = ResolveServiceName(whatsApp, canonical);
                if (UsableLabel(fromService, canonical))
                {
                    return fromService.Trim();
                }
            }

            Person person = TryGetPerson(personStore, canonical, participantJid);
            if (person != null && UsableLabel(person.Name, canonical))
            {
                return person.Name.Trim();
            }

            string fromChat = FindNameOnDirectChat(canonical, whatsApp);
            if (UsableLabel(fromChat, canonical))
            {
                return fromChat.Trim();
            }

            return ShortJidUser(canonical);
        }

        private static GroupMember FindRosterMember(ChatItem groupChat, string canonical, IWhatsAppService whatsApp)
        {
            if (groupChat?.GroupMembers == null || string.IsNullOrWhiteSpace(canonical))
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

                if (JidsMatch(whatsApp, member.Jid, canonical) ||
                    JidsMatch(whatsApp, member.PhoneNumber, canonical) ||
                    JidsMatch(whatsApp, member.Lid, canonical))
                {
                    return member;
                }
            }

            return null;
        }

        private static string FindAvatarOnRoster(ChatItem groupChat, string canonical, IWhatsAppService whatsApp)
        {
            GroupMember member = FindRosterMember(groupChat, canonical, whatsApp);
            return string.IsNullOrWhiteSpace(member?.AvatarUrl) ? null : member.AvatarUrl;
        }

        private static string FindAvatarOnDirectChat(string canonical, IWhatsAppService whatsApp)
        {
            if (whatsApp?.Chats == null || string.IsNullOrWhiteSpace(canonical))
            {
                return null;
            }

            for (int i = 0; i < whatsApp.Chats.Count; i++)
            {
                ChatItem chat = whatsApp.Chats[i];
                if (chat == null || chat.IsGroup || string.IsNullOrWhiteSpace(chat.JID))
                {
                    continue;
                }

                if (!JidsMatch(whatsApp, chat.JID, canonical))
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

        private static string FindAvatarOnPerson(string canonical, string participantJid, IPersonStore personStore)
        {
            Person person = TryGetPerson(personStore, canonical, participantJid);
            return string.IsNullOrWhiteSpace(person?.AvatarUrl) ? null : person.AvatarUrl;
        }

        private static string FindNameOnDirectChat(string canonical, IWhatsAppService whatsApp)
        {
            if (whatsApp?.Chats == null || string.IsNullOrWhiteSpace(canonical))
            {
                return null;
            }

            for (int i = 0; i < whatsApp.Chats.Count; i++)
            {
                ChatItem chat = whatsApp.Chats[i];
                if (chat == null || chat.IsGroup || string.IsNullOrWhiteSpace(chat.JID))
                {
                    continue;
                }

                if (!JidsMatch(whatsApp, chat.JID, canonical))
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

        private static Person TryGetPerson(IPersonStore personStore, string canonical, string participantJid)
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

        private static string ResolveServiceName(IWhatsAppService whatsApp, string jid)
        {
            return whatsApp?.ResolveDisplayName(jid, "sender");
        }

        private static string CanonicalJid(IWhatsAppService whatsApp, string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return null;
            }

            string canonical = whatsApp != null ? whatsApp.GetCanonicalJid(jid) : null;
            if (string.IsNullOrWhiteSpace(canonical))
            {
                canonical = JidHelper.Normalize(jid);
            }

            return canonical;
        }

        private static bool JidsMatch(IWhatsAppService whatsApp, string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            string leftCanon = CanonicalJid(whatsApp, left);
            string rightCanon = CanonicalJid(whatsApp, right);
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

            if (string.Equals(trimmed, "Me", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, "You", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string bare = ShortJidUser(jid);
            if (!string.IsNullOrEmpty(bare) &&
                string.Equals(trimmed, bare, StringComparison.OrdinalIgnoreCase))
            {
                return false;
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
