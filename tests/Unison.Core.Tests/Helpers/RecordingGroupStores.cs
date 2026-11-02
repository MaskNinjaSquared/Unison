using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Models;

namespace Unison.Core.Tests.Helpers
{
    /// <summary>Records roster writes; completes synchronously.</summary>
    internal sealed class RecordingRosterStore : IGroupRosterStore
    {
        public List<KeyValuePair<string, IReadOnlyList<GroupMember>>> Replaced { get; } =
            new List<KeyValuePair<string, IReadOnlyList<GroupMember>>>();

        public bool Throw { get; set; }

        public Task InitializeAsync() => Task.CompletedTask;

        public Task ReplaceForGroupAsync(string groupJid, IReadOnlyList<GroupMember> members)
        {
            if (Throw)
            {
                throw new InvalidOperationException("disk");
            }

            Replaced.Add(new KeyValuePair<string, IReadOnlyList<GroupMember>>(groupJid, members));
            return Task.CompletedTask;
        }

        public List<KeyValuePair<string, IReadOnlyList<GroupMember>>> Upserted { get; } =
            new List<KeyValuePair<string, IReadOnlyList<GroupMember>>>();

        public List<KeyValuePair<string, IReadOnlyList<string>>> Removed { get; } =
            new List<KeyValuePair<string, IReadOnlyList<string>>>();

        public Dictionary<string, GroupMember> Stored { get; } =
            new Dictionary<string, GroupMember>(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyList<GroupMember>> GetForGroupAsync(string groupJid) =>
            Task.FromResult((IReadOnlyList<GroupMember>)new GroupMember[0]);

        public Task<GroupMember> GetMemberAsync(string groupJid, string memberJid)
        {
            GroupMember member;
            return Task.FromResult(
                memberJid != null && Stored.TryGetValue(memberJid, out member) ? member : null);
        }

        public Task UpsertMembersAsync(string groupJid, IReadOnlyList<GroupMember> members)
        {
            if (Throw)
            {
                throw new InvalidOperationException("disk");
            }

            Upserted.Add(new KeyValuePair<string, IReadOnlyList<GroupMember>>(groupJid, members));
            return Task.CompletedTask;
        }

        public Task RemoveMembersAsync(string groupJid, IReadOnlyList<string> memberJids)
        {
            Removed.Add(new KeyValuePair<string, IReadOnlyList<string>>(groupJid, memberJids));
            return Task.CompletedTask;
        }

        public Task ClearAllAsync() => Task.CompletedTask;
    }

    /// <summary>Records group-membership writes; everything else is empty.</summary>
    internal sealed class RecordingPersonStore : IPersonStore
    {
        public List<KeyValuePair<string, IReadOnlyList<PersonGroupMembership>>> Memberships { get; } =
            new List<KeyValuePair<string, IReadOnlyList<PersonGroupMembership>>>();

        public event EventHandler<string> PersonChanged { add { } remove { } }

        public Task InitializeAsync() => Task.CompletedTask;

        public Task<Person> GetAsync(string jid) => Task.FromResult<Person>(null);

        public Person TryGetCached(string jid) => null;

        public Task<bool> UpsertIfChangedAsync(string jid, string name, string avatarUrl, string phone, PersonSource source) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<Person>> FindByPhoneAsync(string digits) =>
            Task.FromResult((IReadOnlyList<Person>)new Person[0]);

        public Task<IReadOnlyList<Person>> ListWithPhoneAsync() =>
            Task.FromResult((IReadOnlyList<Person>)new Person[0]);

        public Task ReplaceGroupMembershipsAsync(string groupJid, IReadOnlyList<PersonGroupMembership> members)
        {
            Memberships.Add(new KeyValuePair<string, IReadOnlyList<PersonGroupMembership>>(groupJid, members));
            return Task.CompletedTask;
        }

        public List<KeyValuePair<string, IReadOnlyList<PersonGroupMembership>>> UpsertedMemberships { get; } =
            new List<KeyValuePair<string, IReadOnlyList<PersonGroupMembership>>>();

        public List<KeyValuePair<string, IReadOnlyList<string>>> RemovedMemberships { get; } =
            new List<KeyValuePair<string, IReadOnlyList<string>>>();

        public Task UpsertGroupMembershipsAsync(string groupJid, IReadOnlyList<PersonGroupMembership> members)
        {
            UpsertedMemberships.Add(new KeyValuePair<string, IReadOnlyList<PersonGroupMembership>>(groupJid, members));
            return Task.CompletedTask;
        }

        public Task RemoveGroupMembershipsAsync(string groupJid, IReadOnlyList<string> personJids)
        {
            RemovedMemberships.Add(new KeyValuePair<string, IReadOnlyList<string>>(groupJid, personJids));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PersonGroupMembership>> ListGroupsForPersonAsync(string personJid) =>
            Task.FromResult((IReadOnlyList<PersonGroupMembership>)new PersonGroupMembership[0]);
    }
}
