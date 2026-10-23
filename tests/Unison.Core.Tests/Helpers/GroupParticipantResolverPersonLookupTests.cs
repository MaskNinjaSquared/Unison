// =============================================================================
// Person rows keyed under the PN/LID alias must still name the group strip.
// Contacts (agenda / shared ContactDisplayNames) outrank a roster tip label.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class GroupParticipantResolverPersonLookupTests
    {
        private const string Pn = "5511988887777@s.whatsapp.net";
        private const string Lid = "199999999999@lid";

        [Fact]
        public void ResolveDisplayName_reads_Person_under_the_alias()
        {
            var jids = new AliasJids();
            jids.Pair(Lid, Pn);

            var people = new MemoryPersonStore();
            people.Cache(new Person
            {
                Jid = Pn,
                Name = "Ana Souza",
                Source = PersonSource.Observed
            });

            var context = new ParticipantResolutionContext(jids, contacts: null, chatState: null, people);

            string name = GroupParticipantResolver.ResolveDisplayName(
                Lid,
                groupChat: null,
                context);

            Assert.Equal("Ana Souza", name);
        }

        [Fact]
        public void Contacts_beat_a_roster_label_so_agenda_and_strip_share_one_origin()
        {
            var jids = new AliasJids();
            var contacts = new StubContacts("Ana Agenda");
            var people = new MemoryPersonStore();
            var context = new ParticipantResolutionContext(jids, contacts, chatState: null, people);

            var member = new GroupMember { Jid = Pn, DisplayName = "Ana Roster" };
            var group = new ChatItem
            {
                JID = "120363@g.us",
                IsGroup = true,
                GroupMembers = new List<GroupMember> { member }
            };

            string name = GroupParticipantResolver.ResolveDisplayName(
                Pn,
                group,
                context,
                nameHint: "Ana Hint",
                rosterMember: member);

            Assert.Equal("Ana Agenda", name);
        }

        private sealed class StubContacts : IContactService
        {
            private readonly string _name;

            public StubContacts(string name) => _name = name;

            public event EventHandler DisplayNamesUpdated
            {
                add { }
                remove { }
            }

            public void NotifyDisplayNamesUpdated()
            {
            }

            public string ResolveDisplayName(string jid, string context) => _name;

            public bool IsSelf(string jid) => false;

            public string ResolvePlaceholderChatName(string jid) => _name;

            public IReadOnlyDictionary<string, string> PhoneContactNamesByJid { get; } =
                new Dictionary<string, string>();

            public void MarkAvatarImageLoadFailed(ChatItem chat, string reason)
            {
            }

            public Task<Dictionary<string, string>> SyncLocalContactsAsync(
                IEnumerable<string> directChatJids,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new Dictionary<string, string>());

            public Task RetrieveContactPicturesAsync(CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task HydrateCachedAvatarUrisAsync(string reason) => Task.CompletedTask;

            public Task HydrateGroupMemberAvatarsAsync(string groupJid) => Task.CompletedTask;

            public Task HydrateGroupMemberAvatarsForJidsAsync(
                string groupJid,
                IReadOnlyList<string> memberJids) =>
                Task.CompletedTask;

            public bool IsHydratingGroupMemberAvatars => false;

            public Task NotifyAvatarCachedAsync(string jid, string localAvatarUrl) => Task.CompletedTask;

            public Task RefreshContactNamesAsync(bool includeGroups = false, bool force = false) =>
                Task.CompletedTask;

            public Task RefreshPhoneContactOverlayAsync(bool force) => Task.CompletedTask;

            public Task RefreshChatListAddressBookNamesAsync() => Task.CompletedTask;

            public bool IsContactRefreshRunning => false;

            public bool IsContactRefreshOnCooldown => false;

            public Task ResolveMissingNamesAsync(bool force = false) => Task.CompletedTask;

            public Task RunBackgroundResolutionAsync(CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task RunDeferredStartupContactPassAsync(bool resolveMissingNames = false) =>
                Task.CompletedTask;

            public void RequestAvatarRefresh(ChatItem chat, bool force = false)
            {
            }

            public void CancelAliasFollowUp()
            {
            }

            public Task ApplyRemoteGroupSubjectsAsync(
                IEnumerable<KeyValuePair<string, string>> subjectsByJid) =>
                Task.CompletedTask;

            public Task ApplyRemoteContactNameAsync(string jid, string name) => Task.CompletedTask;

            public Task ApplyRemoteSelfPushNameAsync(string name) => Task.CompletedTask;

            public Task<string> SearchContactAsync(string phoneNumber) => Task.FromResult<string>(null);

            public string TryResolvePhone(string jid, string phoneDigits = null) => null;

            public bool CanAddToAddressBook(string jid, string phoneDigits = null) => false;

            public Task<bool> ShowAddToAddressBookAsync(
                string displayName,
                string phoneDigits,
                string photoUri = null,
                string jid = null) =>
                Task.FromResult(false);

            public Task SetPublishContactsToWindowsAsync(bool enabled) => Task.CompletedTask;
        }

        private sealed class AliasJids : IJidResolver
        {
            private readonly Dictionary<string, string> _pairs =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public Profile Self => null;

            public void Pair(string left, string right)
            {
                string a = JidHelper.Normalize(left);
                string b = JidHelper.Normalize(right);
                _pairs[a] = b;
                _pairs[b] = a;
            }

            public string GetCanonicalJid(string jid)
            {
                string key = JidHelper.Normalize(jid);
                string alias;
                if (key != null && _pairs.TryGetValue(key, out alias) && JidHelper.IsPhoneJid(alias))
                {
                    return alias;
                }

                return key;
            }

            public bool TryGetAlias(string jid, out string alias)
            {
                string key = JidHelper.Normalize(jid);
                if (key != null && _pairs.TryGetValue(key, out alias))
                {
                    return true;
                }

                alias = null;
                return false;
            }

            public bool IsSelfLinked(string jid) => false;
        }

        private sealed class MemoryPersonStore : IPersonStore
        {
            private readonly Dictionary<string, Person> _cache =
                new Dictionary<string, Person>(StringComparer.OrdinalIgnoreCase);

            public event EventHandler<string> PersonChanged
            {
                add { }
                remove { }
            }

            public void Cache(Person person)
            {
                _cache[JidHelper.Normalize(person.Jid)] = person;
            }

            public Task InitializeAsync() => Task.CompletedTask;

            public Task<Person> GetAsync(string jid)
            {
                Person person;
                return Task.FromResult(
                    _cache.TryGetValue(JidHelper.Normalize(jid), out person) ? person : null);
            }

            public Person TryGetCached(string jid)
            {
                Person person;
                return _cache.TryGetValue(JidHelper.Normalize(jid) ?? string.Empty, out person)
                    ? person
                    : null;
            }

            public Task<bool> UpsertIfChangedAsync(
                string jid,
                string name,
                string avatarUrl,
                string phone,
                PersonSource source) =>
                Task.FromResult(false);

            public Task<IReadOnlyList<Person>> FindByPhoneAsync(string digits) =>
                Task.FromResult((IReadOnlyList<Person>)Array.Empty<Person>());

            public Task<IReadOnlyList<Person>> ListWithPhoneAsync() =>
                Task.FromResult((IReadOnlyList<Person>)Array.Empty<Person>());

            public Task ReplaceGroupMembershipsAsync(
                string groupJid,
                IReadOnlyList<PersonGroupMembership> members) =>
                Task.CompletedTask;

            public Task<IReadOnlyList<PersonGroupMembership>> ListGroupsForPersonAsync(string personJid) =>
                Task.FromResult((IReadOnlyList<PersonGroupMembership>)Array.Empty<PersonGroupMembership>());
        }
    }
}
