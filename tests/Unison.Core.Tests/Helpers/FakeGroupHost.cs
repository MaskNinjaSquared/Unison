using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.Tests.Helpers
{
    /// <summary>Records what the group flows asked the client to do.</summary>
    internal sealed class FakeGroupHost : IGroupHost
    {
        public Dictionary<string, string> MemberNames { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> ChatNames { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> CachedNames { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> RowNames { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> Avatars { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public List<string> RowNameLookups { get; } = new List<string>();

        public List<KeyValuePair<string, List<GroupMember>>> SavedRosters { get; } =
            new List<KeyValuePair<string, List<GroupMember>>>();

        public List<KeyValuePair<string, List<PersonGroupMembership>>> SavedMemberships { get; } =
            new List<KeyValuePair<string, List<PersonGroupMembership>>>();

        public string ResolveMemberDisplayName(string jid)
        {
            string name;
            return MemberNames.TryGetValue(jid, out name) ? name : jid;
        }

        public string ResolveChatDisplayName(string jid)
        {
            string name;
            if (ChatNames.TryGetValue(jid, out name))
            {
                return name;
            }

            return CachedNames.TryGetValue(jid, out name) ? name : jid;
        }

        public bool IsMeaningfulGroupLabel(string label, string groupJid) =>
            !GroupIdPlaceholder.IsIdPlaceholder(label, groupJid);

        public bool HasMeaningfulGroupLabel(string groupJid, string rowName)
        {
            string cached;
            return (CachedNames.TryGetValue(groupJid, out cached) && IsMeaningfulGroupLabel(cached, groupJid)) ||
                   IsMeaningfulGroupLabel(rowName, groupJid);
        }

        public string FindGroupRowName(string groupJid)
        {
            RowNameLookups.Add(groupJid);
            string name;
            return RowNames.TryGetValue(groupJid, out name) ? name : null;
        }

        public void CacheGroupSubject(string groupJid, string subject) => CachedNames[groupJid] = subject;

        public string FindExistingAvatarUrl(string jid, string phoneNumber, string lid)
        {
            string url;
            return Avatars.TryGetValue(jid, out url) ? url : null;
        }

        public void SaveGroupRoster(string groupJid, List<GroupMember> snapshot) =>
            SavedRosters.Add(new KeyValuePair<string, List<GroupMember>>(groupJid, snapshot));

        public void SaveGroupMemberships(string groupJid, List<PersonGroupMembership> rows) =>
            SavedMemberships.Add(new KeyValuePair<string, List<PersonGroupMembership>>(groupJid, rows));
    }
}
