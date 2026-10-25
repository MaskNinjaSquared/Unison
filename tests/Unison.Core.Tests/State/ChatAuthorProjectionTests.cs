// =============================================================================
// ChatAuthorProjection — strip upgrades from phone/LID once Person is reachable.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.Core.Tests.Helpers;
using Xunit;

namespace Unison.Core.Tests.State
{
    public class ChatAuthorProjectionTests
    {
        private const string Group = "120363@g.us";
        private const string Pn = "5511988887777@s.whatsapp.net";
        private const string Lid = "199999999999@lid";

        [Fact]
        public async Task Sweep_names_the_strip_from_Person_under_the_alias()
        {
            var jids = new AliasJids();
            jids.Pair(Lid, Pn);

            var people = new MemoryPersonStore();
            people.SeedDisk(new Person
            {
                Jid = Pn,
                Name = "Ana Souza",
                Phone = "5511988887777",
                Source = PersonSource.Observed
            });

            var chatState = new ChatStateStore(new CountingDispatcher());
            var group = new ChatItem
            {
                JID = Group,
                Name = "Família",
                IsGroup = true,
                LastMessageIsFromMe = false,
                LastMessageParticipantJid = Lid,
                LastMessageAuthor = "5511988887777: "
            };
            chatState.Chats.Add(group);

            var projection = new ChatAuthorProjection(
                chatState,
                people,
                jids,
                strings: null,
                new CountingDispatcher());

            projection.Start();
            await WaitForAsync(() => group.LastMessageSenderName == "Ana Souza");

            Assert.Equal("Ana Souza: ", group.LastMessageAuthor);
        }

        [Fact]
        public async Task Sweep_names_the_strip_from_the_1to1_chat()
        {
            var jids = new AliasJids();
            var people = new MemoryPersonStore();
            var chatState = new ChatStateStore(new CountingDispatcher());

            chatState.Chats.Add(new ChatItem
            {
                JID = Pn,
                Name = "Ana Souza",
                IsGroup = false
            });

            // Decoy DMs: without a 1:1 name index the strip walk would scan these every group.
            for (int i = 0; i < 40; i++)
            {
                chatState.Chats.Add(new ChatItem
                {
                    JID = "551190000" + i.ToString("0000") + "@s.whatsapp.net",
                    Name = "Decoy " + i,
                    IsGroup = false
                });
            }

            var group = new ChatItem
            {
                JID = Group,
                Name = "Família",
                IsGroup = true,
                LastMessageIsFromMe = false,
                LastMessageParticipantJid = Pn
            };
            chatState.Chats.Add(group);

            var projection = new ChatAuthorProjection(
                chatState,
                people,
                jids,
                strings: null,
                new CountingDispatcher());

            projection.Start();
            await WaitForAsync(() => group.LastMessageSenderName == "Ana Souza");

            Assert.Equal("Ana Souza: ", group.LastMessageAuthor);
        }

        [Fact]
        public async Task Sweep_names_the_strip_from_roster_DisplayName()
        {
            var jids = new AliasJids();
            var people = new MemoryPersonStore();
            var chatState = new ChatStateStore(new CountingDispatcher());

            var group = new ChatItem
            {
                JID = Group,
                Name = "Família",
                IsGroup = true,
                LastMessageIsFromMe = false,
                LastMessageParticipantJid = Lid,
                GroupMembers = new List<GroupMember>
                {
                    new GroupMember
                    {
                        Jid = Lid,
                        DisplayName = "Ana Roster"
                    }
                }
            };
            chatState.Chats.Add(group);

            var projection = new ChatAuthorProjection(
                chatState,
                people,
                jids,
                strings: null,
                new CountingDispatcher());

            projection.Start();
            await WaitForAsync(() => group.LastMessageSenderName == "Ana Roster");

            Assert.Equal("Ana Roster: ", group.LastMessageAuthor);
        }

        [Fact]
        public async Task OnDemand_PersonChanged_updates_only_groups_with_that_tip()
        {
            var jids = new AliasJids();
            jids.Pair(Lid, Pn);
            var people = new MemoryPersonStore();
            people.SeedDisk(new Person
            {
                Jid = Pn,
                Name = "Ana Souza",
                Phone = "5511988887777",
                Source = PersonSource.Observed
            });

            var chatState = new ChatStateStore(new CountingDispatcher());
            var touched = new ChatItem
            {
                JID = Group,
                Name = "Família",
                IsGroup = true,
                LastMessageIsFromMe = false,
                LastMessageParticipantJid = Lid,
                LastMessageAuthor = "5511988887777: "
            };
            var other = new ChatItem
            {
                JID = "120364@g.us",
                Name = "Outro",
                IsGroup = true,
                LastMessageIsFromMe = false,
                LastMessageParticipantJid = "5511911112222@s.whatsapp.net",
                LastMessageSenderName = "Outro Tip",
                LastMessageAuthor = "Outro Tip: "
            };
            chatState.Chats.Add(touched);
            chatState.Chats.Add(other);

            var projection = new ChatAuthorProjection(
                chatState,
                people,
                jids,
                strings: null,
                new CountingDispatcher(),
                onDemand: true);

            projection.Start();
            await Task.Delay(30);
            Assert.Null(touched.LastMessageSenderName);
            Assert.Equal("Outro Tip: ", other.LastMessageAuthor);

            // Warm Person into cache, then signal only Ana's JID.
            await people.GetAsync(Pn);
            // Raise via the store the projection subscribed to — simulate PersonChanged.
            people.RaisePersonChanged(Pn);
            await WaitForAsync(() => touched.LastMessageSenderName == "Ana Souza");

            Assert.Equal("Ana Souza: ", touched.LastMessageAuthor);
            Assert.Equal("Outro Tip: ", other.LastMessageAuthor);
            Assert.Equal("Outro Tip", other.LastMessageSenderName);
        }

        [Fact]
        public async Task OnDemand_RefreshChat_updates_only_that_group()
        {
            var jids = new AliasJids();
            var people = new MemoryPersonStore();
            var chatState = new ChatStateStore(new CountingDispatcher());

            chatState.Chats.Add(new ChatItem
            {
                JID = Pn,
                Name = "Ana Souza",
                IsGroup = false
            });

            var target = new ChatItem
            {
                JID = Group,
                Name = "Família",
                IsGroup = true,
                LastMessageIsFromMe = false,
                LastMessageParticipantJid = Pn
            };
            var other = new ChatItem
            {
                JID = "120364@g.us",
                Name = "Outro",
                IsGroup = true,
                LastMessageIsFromMe = false,
                LastMessageParticipantJid = "5511911112222@s.whatsapp.net",
                LastMessageSenderName = "Keep Me",
                LastMessageAuthor = "Keep Me: "
            };
            chatState.Chats.Add(target);
            chatState.Chats.Add(other);

            var projection = new ChatAuthorProjection(
                chatState,
                people,
                jids,
                strings: null,
                new CountingDispatcher(),
                onDemand: true);

            projection.Start();
            projection.RefreshChat(Group);
            await WaitForAsync(() => target.LastMessageSenderName == "Ana Souza");

            Assert.Equal("Ana Souza: ", target.LastMessageAuthor);
            Assert.Equal("Keep Me: ", other.LastMessageAuthor);
        }

        [Fact]
        public async Task DisplayNamesChanged_retries_warm_after_a_miss_under_the_LID()
        {
            var jids = new AliasJids();
            var people = new MemoryPersonStore();
            people.SeedDisk(new Person
            {
                Jid = Pn,
                Name = "Ana Souza",
                Phone = "5511988887777",
                Source = PersonSource.Observed
            });

            var chatState = new ChatStateStore(new CountingDispatcher());
            var group = new ChatItem
            {
                JID = Group,
                Name = "Família",
                IsGroup = true,
                LastMessageIsFromMe = false,
                LastMessageParticipantJid = Lid
            };
            chatState.Chats.Add(group);

            var projection = new ChatAuthorProjection(
                chatState,
                people,
                jids,
                strings: null,
                new CountingDispatcher());

            projection.Start();
            await Task.Delay(50);
            Assert.Null(group.LastMessageSenderName);

            // Alias arrives after the first warm burned the LID key with no row.
            jids.Pair(Lid, Pn);
            chatState.NotifyDisplayNamesChanged();
            await WaitForAsync(() => group.LastMessageSenderName == "Ana Souza");
        }

        private static async Task WaitForAsync(Func<bool> condition)
        {
            for (int i = 0; i < 100; i++)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(10);
            }

            Assert.True(condition(), "timed out waiting for strip upgrade");
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

        /// <summary>
        /// Disk-backed fake: GetAsync fills the cache the same way PersonStore does; TryGetCached
        /// stays cold until warm runs.
        /// </summary>
        private sealed class MemoryPersonStore : IPersonStore
        {
            private readonly Dictionary<string, Person> _disk =
                new Dictionary<string, Person>(StringComparer.OrdinalIgnoreCase);

            private readonly Dictionary<string, Person> _cache =
                new Dictionary<string, Person>(StringComparer.OrdinalIgnoreCase);

            public event EventHandler<string> PersonChanged;

            public void RaisePersonChanged(string jid) => PersonChanged?.Invoke(this, jid);

            public void SeedDisk(Person person)
            {
                _disk[JidHelper.Normalize(person.Jid)] = Clone(person);
            }

            public Task InitializeAsync() => Task.CompletedTask;

            public Task<Person> GetAsync(string jid)
            {
                string key = JidHelper.Normalize(jid);
                Person cached;
                if (_cache.TryGetValue(key, out cached))
                {
                    return Task.FromResult(Clone(cached));
                }

                Person row;
                if (!_disk.TryGetValue(key, out row))
                {
                    return Task.FromResult<Person>(null);
                }

                _cache[key] = Clone(row);
                return Task.FromResult(Clone(row));
            }

            public Person TryGetCached(string jid)
            {
                Person person;
                return _cache.TryGetValue(JidHelper.Normalize(jid) ?? string.Empty, out person)
                    ? Clone(person)
                    : null;
            }

            public Task<bool> UpsertIfChangedAsync(
                string jid,
                string name,
                string avatarUrl,
                string phone,
                PersonSource source) =>
                Task.FromResult(false);

            public Task<IReadOnlyList<Person>> FindByPhoneAsync(string digits)
            {
                string phone = PhoneNumberHelper.NormalizePhoneDigits(digits);
                var hits = new List<Person>();
                foreach (Person person in _disk.Values)
                {
                    if (person != null &&
                        string.Equals(person.Phone, phone, StringComparison.Ordinal))
                    {
                        hits.Add(Clone(person));
                    }
                }

                return Task.FromResult((IReadOnlyList<Person>)hits);
            }

            public Task<IReadOnlyList<Person>> ListWithPhoneAsync() =>
                Task.FromResult((IReadOnlyList<Person>)Array.Empty<Person>());

            public Task ReplaceGroupMembershipsAsync(
                string groupJid,
                IReadOnlyList<PersonGroupMembership> members) =>
                Task.CompletedTask;

            public Task<IReadOnlyList<PersonGroupMembership>> ListGroupsForPersonAsync(string personJid) =>
                Task.FromResult((IReadOnlyList<PersonGroupMembership>)Array.Empty<PersonGroupMembership>());

            private static Person Clone(Person source)
            {
                if (source == null)
                {
                    return null;
                }

                return new Person
                {
                    Jid = source.Jid,
                    Name = source.Name,
                    AvatarUrl = source.AvatarUrl,
                    Phone = source.Phone,
                    Source = source.Source,
                    UpdatedAtUtc = source.UpdatedAtUtc
                };
            }
        }
    }
}
