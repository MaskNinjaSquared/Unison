using System;
using System.Collections.Generic;
using System.ComponentModel;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Per open-chat lookup of group participant labels/avatars and 1:1 chat indexes.
    /// Built from the roster (plus service/Person caches) and reused by run layout so every
    /// bubble does not walk the member list and ResolveDisplayName again.
    /// </summary>
    public sealed class GroupParticipantLookup : IDisposable
    {
        private readonly IWhatsAppService _whatsApp;
        private readonly IPersonStore _personStore;
        private readonly Func<string> _selfDisplayLabel;

        private Dictionary<string, string> _participantNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, string> _participantAvatars =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, GroupMember> _rosterByCanonical =
            new Dictionary<string, GroupMember>(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, string> _directChatAvatars =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, string> _directChatNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private List<GroupMember> _hookedRosterMembers;
        private PropertyChangedEventHandler _rosterAvatarChangedHandler;

        private int _participantLookupRosterCount = -1;
        private string _participantLookupChatJid;
        private IReadOnlyDictionary<string, string> _participantLookupMentionRef;

        private bool _disposed;

        public GroupParticipantLookup(
            IWhatsAppService whatsApp,
            IPersonStore personStore,
            Func<string> selfDisplayLabel)
        {
            _whatsApp = whatsApp;
            _personStore = personStore;
            _selfDisplayLabel = selfDisplayLabel ?? (() => "You");
        }

        /// <summary>Raised when a roster member's avatar URL changes (jid, url).</summary>
        public event Action<string, string> ParticipantAvatarChanged;

        public void Rebuild(ChatItem chat)
        {
            ThrowIfDisposed();
            UnhookRosterAvatarChanges();

            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var avatars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var roster = new Dictionary<string, GroupMember>(StringComparer.OrdinalIgnoreCase);

            _participantLookupChatJid = chat?.JID;
            _participantLookupRosterCount = chat?.GroupMembers?.Count ?? 0;
            _participantLookupMentionRef = chat?.MentionLookup;

            RebuildDirectChatIndex();

            if (chat?.GroupMembers == null || chat.GroupMembers.Count == 0)
            {
                _participantNames = names;
                _participantAvatars = avatars;
                _rosterByCanonical = roster;
                SeedSelfParticipantName();
                return;
            }

            for (int i = 0; i < chat.GroupMembers.Count; i++)
            {
                GroupMember member = chat.GroupMembers[i];
                if (member == null || string.IsNullOrWhiteSpace(member.Jid))
                {
                    continue;
                }

                string name = member.DisplayName;
                string avatar = member.AvatarUrl;

                IndexParticipantKey(roster, names, avatars, member.Jid, member, name, avatar);
                IndexParticipantKey(roster, names, avatars, member.Lid, member, name, avatar);
                IndexParticipantKey(roster, names, avatars, member.PhoneNumber, member, name, avatar);

                string canonical = _whatsApp != null
                    ? _whatsApp.GetCanonicalJid(member.Jid)
                    : JidHelper.Normalize(member.Jid);
                IndexParticipantKey(roster, names, avatars, canonical, member, name, avatar);
            }

            _participantNames = names;
            _participantAvatars = avatars;
            _rosterByCanonical = roster;
            SeedSelfParticipantName();
            HookRosterAvatarChanges(chat.GroupMembers);
        }

        public void EnsureFresh(ChatItem chat)
        {
            ThrowIfDisposed();
            int rosterCount = chat?.GroupMembers?.Count ?? 0;
            string jid = chat?.JID;
            IReadOnlyDictionary<string, string> mentionLookup = chat?.MentionLookup;
            if (_participantLookupRosterCount == rosterCount &&
                string.Equals(_participantLookupChatJid, jid, StringComparison.OrdinalIgnoreCase) &&
                _participantNames != null &&
                object.ReferenceEquals(_participantLookupMentionRef, mentionLookup))
            {
                return;
            }

            Rebuild(chat);
        }

        public GroupMember Find(string participantJid)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(participantJid))
            {
                return null;
            }

            string canonical = _whatsApp != null
                ? _whatsApp.GetCanonicalJid(participantJid)
                : JidHelper.Normalize(participantJid);

            GroupMember member;
            if (!string.IsNullOrWhiteSpace(canonical) &&
                _rosterByCanonical.TryGetValue(canonical, out member))
            {
                return member;
            }

            if (_rosterByCanonical.TryGetValue(participantJid, out member))
            {
                return member;
            }

            return null;
        }

        public bool TryGetName(string jid, out string name)
        {
            ThrowIfDisposed();
            name = null;
            if (string.IsNullOrWhiteSpace(jid))
            {
                return false;
            }

            if (SelfIdentity.IsSelf(jid, _whatsApp))
            {
                name = SelfDisplayLabel();
                return true;
            }

            if (_participantNames == null)
            {
                return false;
            }

            if (_participantNames.TryGetValue(jid, out name) &&
                !string.IsNullOrWhiteSpace(name))
            {
                return true;
            }

            string canonical = _whatsApp != null
                ? _whatsApp.GetCanonicalJid(jid)
                : JidHelper.Normalize(jid);
            return !string.IsNullOrWhiteSpace(canonical) &&
                   _participantNames.TryGetValue(canonical, out name) &&
                   !string.IsNullOrWhiteSpace(name);
        }

        public bool TryGetAvatar(string jid, out string avatar)
        {
            ThrowIfDisposed();
            avatar = null;
            if (string.IsNullOrWhiteSpace(jid) || _participantAvatars == null)
            {
                return false;
            }

            if (_participantAvatars.TryGetValue(jid, out avatar) &&
                !string.IsNullOrWhiteSpace(avatar))
            {
                return true;
            }

            string canonical = _whatsApp != null
                ? _whatsApp.GetCanonicalJid(jid)
                : JidHelper.Normalize(jid);
            return !string.IsNullOrWhiteSpace(canonical) &&
                   _participantAvatars.TryGetValue(canonical, out avatar) &&
                   !string.IsNullOrWhiteSpace(avatar);
        }

        public string ResolveContactUri(string jid, ChatItem groupChat)
        {
            ThrowIfDisposed();
            EnsureFresh(groupChat);
            if (string.IsNullOrWhiteSpace(jid))
            {
                return null;
            }

            GroupMember roster = Find(jid);
            if (!string.IsNullOrWhiteSpace(roster?.AvatarUrl))
            {
                CacheAvatar(jid, roster.AvatarUrl);
                return roster.AvatarUrl;
            }

            string avatar;
            if (TryGetAvatar(jid, out avatar))
            {
                return avatar;
            }

            if (TryGetDirectChatAvatar(jid, out avatar))
            {
                CacheAvatar(jid, avatar);
                return avatar;
            }

            string resolved = GroupParticipantResolver.ResolveAvatar(
                jid,
                groupChat,
                _whatsApp,
                _personStore,
                roster,
                _directChatAvatars);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                CacheAvatar(jid, resolved);
            }

            return resolved;
        }

        public void EnsureGroupSenderName(ChatMessage message, ChatItem groupChat)
        {
            ThrowIfDisposed();
            if (message == null || message.IsFromMe)
            {
                return;
            }

            string participant = message.ParticipantJid;
            if (string.IsNullOrWhiteSpace(participant))
            {
                return;
            }

            EnsureFresh(groupChat);

            string cached;
            if (TryGetName(participant, out cached))
            {
                message.SenderName = cached;
                return;
            }

            GroupMember roster = Find(participant);
            if (!string.IsNullOrWhiteSpace(roster?.DisplayName) &&
                roster.DisplayName.IndexOf('@') < 0)
            {
                message.SenderName = roster.DisplayName.Trim();
                CacheName(participant, message.SenderName);
                return;
            }

            string fromDirect;
            if (TryGetDirectChatName(participant, out fromDirect))
            {
                message.SenderName = fromDirect;
                CacheName(participant, fromDirect);
                return;
            }

            string resolved = GroupParticipantResolver.ResolveDisplayName(
                participant,
                groupChat,
                _whatsApp,
                _personStore,
                message.SenderName,
                roster,
                _directChatNames);

            if (!string.IsNullOrWhiteSpace(resolved))
            {
                message.SenderName = resolved;
                CacheName(participant, resolved);
            }
        }

        public void EnsureQuotedSenderName(
            ChatMessage message,
            ChatItem groupChat,
            Func<string, bool> quotedMessageIdIsFromMe)
        {
            ThrowIfDisposed();
            if (message == null || !message.HasQuote)
            {
                return;
            }

            EnsureFresh(groupChat);

            if (IsQuotedMessageFromMe(message, quotedMessageIdIsFromMe))
            {
                message.QuotedSenderName = SelfDisplayLabel();
                return;
            }

            string participant = message.QuotedParticipantJid;
            if (string.IsNullOrWhiteSpace(participant))
            {
                return;
            }

            string cached;
            if (TryGetName(participant, out cached))
            {
                message.QuotedSenderName = cached;
                return;
            }

            GroupMember roster = Find(participant);
            if (!string.IsNullOrWhiteSpace(roster?.DisplayName) &&
                roster.DisplayName.IndexOf('@') < 0)
            {
                message.QuotedSenderName = roster.DisplayName.Trim();
                CacheName(participant, message.QuotedSenderName);
                return;
            }

            string fromDirect;
            if (TryGetDirectChatName(participant, out fromDirect))
            {
                message.QuotedSenderName = fromDirect;
                CacheName(participant, fromDirect);
                return;
            }

            string resolved = GroupParticipantResolver.ResolveDisplayName(
                participant,
                groupChat,
                _whatsApp,
                _personStore,
                message.QuotedSenderName,
                roster,
                _directChatNames);

            if (!string.IsNullOrWhiteSpace(resolved))
            {
                message.QuotedSenderName = resolved;
                CacheName(participant, resolved);
            }
        }

        public void CacheName(string jid, string name)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(jid) || string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            if (SelfIdentity.IsSelf(jid, _whatsApp))
            {
                name = SelfDisplayLabel();
            }

            if (_participantNames == null)
            {
                _participantNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            string norm = JidHelper.Normalize(jid) ?? jid;
            _participantNames[norm] = name;
            string canonical = _whatsApp != null
                ? _whatsApp.GetCanonicalJid(jid)
                : null;
            if (!string.IsNullOrWhiteSpace(canonical))
            {
                _participantNames[canonical] = name;
            }
        }

        public void CacheAvatar(string jid, string avatar)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(jid) || string.IsNullOrWhiteSpace(avatar))
            {
                return;
            }

            if (_participantAvatars == null)
            {
                _participantAvatars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            string norm = JidHelper.Normalize(jid) ?? jid;
            _participantAvatars[norm] = avatar;
            string canonical = _whatsApp != null
                ? _whatsApp.GetCanonicalJid(jid)
                : null;
            if (!string.IsNullOrWhiteSpace(canonical))
            {
                _participantAvatars[canonical] = avatar;
            }
        }

        /// <summary>
        /// Unhooks roster PropertyChanged without disposing — safe for long-lived VMs that reopen chats.
        /// </summary>
        public void DetachHooks()
        {
            if (_disposed)
            {
                return;
            }

            UnhookRosterAvatarChanges();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            UnhookRosterAvatarChanges();
            ParticipantAvatarChanged = null;
            _disposed = true;
        }

        private void RebuildDirectChatIndex()
        {
            var avatars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var chats = _whatsApp?.Chats;
            if (chats == null || chats.Count == 0)
            {
                _directChatAvatars = avatars;
                _directChatNames = names;
                return;
            }

            for (int i = 0; i < chats.Count; i++)
            {
                ChatItem chat = chats[i];
                if (chat == null || chat.IsGroup || string.IsNullOrWhiteSpace(chat.JID))
                {
                    continue;
                }

                string raw = JidHelper.Normalize(chat.JID) ?? chat.JID;
                string canonical = _whatsApp != null
                    ? _whatsApp.GetCanonicalJid(chat.JID)
                    : raw;
                if (string.IsNullOrWhiteSpace(canonical))
                {
                    canonical = raw;
                }

                string url = chat.GetAvatarUrl(preferHigh: false);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    IndexDirectMap(avatars, raw, url);
                    IndexDirectMap(avatars, canonical, url);
                }

                if (!string.IsNullOrWhiteSpace(chat.Name) &&
                    chat.Name.IndexOf('@') < 0 &&
                    !string.Equals(chat.Name, "Me", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(chat.Name, "You", StringComparison.OrdinalIgnoreCase))
                {
                    IndexDirectMap(names, raw, chat.Name.Trim());
                    IndexDirectMap(names, canonical, chat.Name.Trim());
                }
            }

            _directChatAvatars = avatars;
            _directChatNames = names;
        }

        private static void IndexDirectMap(Dictionary<string, string> map, string key, string value)
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

        private void HookRosterAvatarChanges(IList<GroupMember> members)
        {
            if (members == null || members.Count == 0)
            {
                return;
            }

            if (_rosterAvatarChangedHandler == null)
            {
                _rosterAvatarChangedHandler = OnRosterMemberPropertyChanged;
            }

            var hooked = new List<GroupMember>(members.Count);
            for (int i = 0; i < members.Count; i++)
            {
                GroupMember member = members[i];
                if (member == null)
                {
                    continue;
                }

                member.PropertyChanged += _rosterAvatarChangedHandler;
                hooked.Add(member);
            }

            _hookedRosterMembers = hooked;
        }

        private void UnhookRosterAvatarChanges()
        {
            if (_hookedRosterMembers == null || _rosterAvatarChangedHandler == null)
            {
                _hookedRosterMembers = null;
                return;
            }

            for (int i = 0; i < _hookedRosterMembers.Count; i++)
            {
                GroupMember member = _hookedRosterMembers[i];
                if (member != null)
                {
                    member.PropertyChanged -= _rosterAvatarChangedHandler;
                }
            }

            _hookedRosterMembers = null;
        }

        private void OnRosterMemberPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null ||
                e.PropertyName != nameof(GroupMember.AvatarUrl) ||
                !(sender is GroupMember member) ||
                string.IsNullOrWhiteSpace(member.AvatarUrl))
            {
                return;
            }

            string jid = FirstNonEmpty(member.Jid, member.Lid, member.PhoneNumber);
            if (string.IsNullOrWhiteSpace(jid))
            {
                return;
            }

            CacheAvatar(jid, member.AvatarUrl);
            ParticipantAvatarChanged?.Invoke(jid, member.AvatarUrl);
        }

        private static void IndexParticipantKey(
            Dictionary<string, GroupMember> roster,
            Dictionary<string, string> names,
            Dictionary<string, string> avatars,
            string key,
            GroupMember member,
            string name,
            string avatar)
        {
            if (string.IsNullOrWhiteSpace(key) || member == null)
            {
                return;
            }

            string norm = JidHelper.Normalize(key) ?? key;
            if (string.IsNullOrWhiteSpace(norm))
            {
                return;
            }

            roster[norm] = member;
            if (!string.IsNullOrWhiteSpace(name))
            {
                names[norm] = name;
            }

            if (!string.IsNullOrWhiteSpace(avatar))
            {
                avatars[norm] = avatar;
            }
        }

        private void SeedSelfParticipantName()
        {
            if (_participantNames == null)
            {
                _participantNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            Profile me = _whatsApp?.CurrentProfile;
            if (me == null)
            {
                return;
            }

            string label = SelfDisplayLabel();
            IndexSelfKey(me.Id, label);
            IndexSelfKey(me.Lid, label);
        }

        private void IndexSelfKey(string rawJid, string label)
        {
            if (string.IsNullOrWhiteSpace(rawJid) || string.IsNullOrWhiteSpace(label))
            {
                return;
            }

            string norm = JidHelper.Normalize(rawJid) ?? rawJid;
            _participantNames[norm] = label;
            string canonical = _whatsApp != null
                ? _whatsApp.GetCanonicalJid(rawJid)
                : null;
            if (!string.IsNullOrWhiteSpace(canonical))
            {
                _participantNames[canonical] = label;
            }
        }

        private bool IsQuotedMessageFromMe(ChatMessage message, Func<string, bool> quotedMessageIdIsFromMe)
        {
            if (message == null)
            {
                return false;
            }

            if (SelfIdentity.IsSelf(message.QuotedParticipantJid, _whatsApp))
            {
                return true;
            }

            string quotedId = message.QuotedMessageId;
            if (string.IsNullOrWhiteSpace(quotedId) || quotedMessageIdIsFromMe == null)
            {
                return false;
            }

            return quotedMessageIdIsFromMe(quotedId);
        }

        private bool TryGetDirectChatAvatar(string participantJid, out string avatar)
        {
            avatar = null;
            if (string.IsNullOrWhiteSpace(participantJid) || _directChatAvatars == null)
            {
                return false;
            }

            if (_directChatAvatars.TryGetValue(participantJid, out avatar) &&
                !string.IsNullOrWhiteSpace(avatar))
            {
                return true;
            }

            string canonical = _whatsApp != null
                ? _whatsApp.GetCanonicalJid(participantJid)
                : JidHelper.Normalize(participantJid);
            return !string.IsNullOrWhiteSpace(canonical) &&
                   _directChatAvatars.TryGetValue(canonical, out avatar) &&
                   !string.IsNullOrWhiteSpace(avatar);
        }

        private bool TryGetDirectChatName(string participantJid, out string name)
        {
            name = null;
            if (string.IsNullOrWhiteSpace(participantJid) || _directChatNames == null)
            {
                return false;
            }

            if (_directChatNames.TryGetValue(participantJid, out name) &&
                !string.IsNullOrWhiteSpace(name))
            {
                return true;
            }

            string canonical = _whatsApp != null
                ? _whatsApp.GetCanonicalJid(participantJid)
                : JidHelper.Normalize(participantJid);
            return !string.IsNullOrWhiteSpace(canonical) &&
                   _directChatNames.TryGetValue(canonical, out name) &&
                   !string.IsNullOrWhiteSpace(name);
        }

        private string SelfDisplayLabel()
        {
            string label = _selfDisplayLabel?.Invoke();
            return string.IsNullOrWhiteSpace(label) ? "You" : label;
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

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(GroupParticipantLookup));
            }
        }
    }
}
