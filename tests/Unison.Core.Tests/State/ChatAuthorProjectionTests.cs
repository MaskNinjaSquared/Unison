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
        public async Task Sweep_clears_author_prefix_on_group_system_tips()
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
                LastMessageKind = ChatPreviewKind.GroupJoin,
                LastMessage = "Ana Souza removeu Beltrano",
                LastMessageParticipantJid = Lid,
                LastMessageAuthor = "Ana Souza: ",
                LastMessageSenderName = "Ana Souza"
            };
            chatState.Chats.Add(group);

            var projection = new ChatAuthorProjection(
                chatState,
                people,
                jids,
                strings: null,
                new CountingDispatcher());

            projection.Start();
            await WaitForAsync(() => group.LastMessageAuthor == string.Empty);

            Assert.Equal(string.Empty, group.LastMessageAuthor);
            // Body already names the actor — keep SenderName, but never "Author: " chip.
            Assert.Equal("Ana Souza", group.LastMessageSenderName);
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
        public async Task Sweep_finds_the_tip_at_the_end_of_a_large_roster()
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

            var members = new List<GroupMember>();
            for (int i = 0; i < 80; i++)
            {
                members.Add(new GroupMember
                {
                    Jid = "1000" + i + "@lid",
                    DisplayName = "Member " + i
                });
            }

            // Tip at the end: the address index reaches it however big the roster is.
            members.Add(new GroupMember
            {
                Jid = Lid,
                DisplayName = "Ana Roster Only"
            });

            var chatState = new ChatStateStore(new CountingDispatcher());
            var group = new ChatItem
            {
                JID = Group,
                Name = "Família",
                IsGroup = true,
                LastMessageIsFromMe = false,
                LastMessageParticipantJid = Lid,
                GroupMembers = members
            };
            chatState.Chats.Add(group);

            var dispatcher = new CountingDispatcher();
            var projection = new ChatAuthorProjection(
                chatState,
                people,
                jids,
                strings: null,
                dispatcher);

            projection.Start();
            await WaitForAsync(() => group.LastMessageSenderName == "Ana Roster Only");

            Assert.Equal("Ana Roster Only: ", group.LastMessageAuthor);
            Assert.True(dispatcher.MaxDepth <= 1, "BeginSweep nested depth=" + dispatcher.MaxDepth);
        }

        [Fact]
        public async Task Start_names_unnamed_strips_and_leaves_named_ones_alone()
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
                new CountingDispatcher());

            projection.Start();
            await WaitForAsync(() => touched.LastMessageSenderName == "Ana Souza");

            Assert.Equal("Ana Souza: ", touched.LastMessageAuthor);
            Assert.Equal("Outro Tip: ", other.LastMessageAuthor);
            Assert.Equal("Outro Tip", other.LastMessageSenderName);
        }

        [Fact]
        public async Task RefreshChat_updates_only_that_group()
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
                new CountingDispatcher());

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

        [Fact]
        public async Task RefreshChat_without_Start_names_only_the_pending_group()
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

            for (int i = 0; i < 120; i++)
            {
                chatState.Chats.Add(new ChatItem
                {
                    JID = "120399" + i + "@g.us",
                    Name = "Decoy " + i,
                    IsGroup = true,
                    LastMessageIsFromMe = false,
                    LastMessageParticipantJid = "551190000" + i.ToString("0000") + "@s.whatsapp.net"
                });
            }

            var target = new ChatItem
            {
                JID = Group,
                Name = "Família",
                IsGroup = true,
                LastMessageIsFromMe = false,
                LastMessageParticipantJid = Pn
            };
            chatState.Chats.Add(target);

            var dispatcher = new CountingDispatcher();
            var projection = new ChatAuthorProjection(
                chatState,
                people,
                jids,
                strings: null,
                dispatcher);

            // No Start(): unnamed catalogue walk must not run — only RefreshChat pending.
            projection.RefreshChat(Group);
            await WaitForAsync(() => target.LastMessageSenderName == "Ana Souza");

            Assert.Equal("Ana Souza: ", target.LastMessageAuthor);
            Assert.True(dispatcher.MaxDepth <= 1, "nested depth=" + dispatcher.MaxDepth);
            Assert.True(dispatcher.Passes <= 4, "passes=" + dispatcher.Passes);
        }

        [Fact]
        public async Task DisplayNamesChanged_under_Hold_does_not_schedule_unnamed_until_Release()
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
            // Warm the in-memory cache so the deferred unnamed pass resolves in one hop.
            await people.GetAsync(Pn);

            var gate = new ChatTimelineLoadPriorityGate();
            gate.Hold("open");

            var chatState = new ChatStateStore(new CountingDispatcher());
            var unnamed = new ChatItem
            {
                JID = Group,
                Name = "Família",
                IsGroup = true,
                LastMessageIsFromMe = false,
                LastMessageParticipantJid = Lid
            };
            chatState.Chats.Add(unnamed);

            var dispatcher = new CountingDispatcher();
            var projection = new ChatAuthorProjection(
                chatState,
                people,
                jids,
                strings: null,
                dispatcher,
                timelinePriority: gate,
                preferFrugalUi: () => true);

            // Start under Hold+frugal: unnamed is deferred (no dispatcher hop yet).
            projection.Start();
            chatState.NotifyDisplayNamesChanged();
            await Task.Delay(250);

            Assert.Null(unnamed.LastMessageSenderName);
            Assert.Equal(0, dispatcher.Passes);

            gate.Release("open");
            await WaitForAsync(() => unnamed.LastMessageSenderName == "Ana Souza");
        }

        [Fact]
        public async Task RefreshChat_under_frugal_Hold_stays_pending_fast_path_after_DisplayNamesChanged()
        {
            var jids = new AliasJids();
            var people = new MemoryPersonStore();
            var gate = new ChatTimelineLoadPriorityGate();
            gate.Hold("open");

            var chatState = new ChatStateStore(new CountingDispatcher());
            chatState.Chats.Add(new ChatItem
            {
                JID = Pn,
                Name = "Ana Souza",
                IsGroup = false
            });

            var decoy = new ChatItem
            {
                JID = "120364@g.us",
                Name = "Outro",
                IsGroup = true,
                LastMessageIsFromMe = false,
                LastMessageParticipantJid = Lid,
                LastMessageAuthor = "199999999999: "
            };
            var target = new ChatItem
            {
                JID = Group,
                Name = "Família",
                IsGroup = true,
                LastMessageIsFromMe = false,
                LastMessageParticipantJid = Pn
            };
            chatState.Chats.Add(decoy);
            chatState.Chats.Add(target);

            var projection = new ChatAuthorProjection(
                chatState,
                people,
                jids,
                strings: null,
                new CountingDispatcher(),
                timelinePriority: gate,
                preferFrugalUi: () => true);

            projection.Start();
            // Would have forced a full unnamed walk before the open RefreshChat.
            chatState.NotifyDisplayNamesChanged();
            projection.RefreshChat(Group);

            gate.Release("open");
            await WaitForAsync(() => target.LastMessageSenderName == "Ana Souza");

            // Decoy still unnamed: DisplayNamesChanged was deferred and must not have ridden
            // along inside the pending-chat pass (a later flushed unnamed walk may still touch it).
            Assert.Null(decoy.LastMessageSenderName);
        }

        /// <summary>
        /// The device shape. The row already knows the sender's name, but the rendered prefix is
        /// their digits: the two are separate columns and the store carries each forward on its
        /// own, so a tip with no push name leaves digits beside a good name. Decide reads only
        /// the name, so it reports Keep — and the prefix must still be rewritten.
        /// </summary>
        [Fact]
        public async Task Sweep_rewrites_a_digits_prefix_that_sits_beside_a_known_name()
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
                LastMessageParticipantJid = Pn,
                LastMessageSenderName = "Ana Souza",
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
            await WaitForAsync(() => group.LastMessageAuthor == "Ana Souza: ");

            Assert.Equal("Ana Souza: ", group.LastMessageAuthor);
            Assert.Equal("Ana Souza", group.LastMessageSenderName);
        }

        /// <summary>
        /// The regression the repair must not cause. When the tip moves to a participant with no
        /// push name, the store carries the *previous* sender's name forward beside the *new*
        /// sender's digits — the two fields then describe two different people. Rewriting the
        /// prefix from the stored name would put Ana's name on Beto's message, which is worse
        /// than digits: silently wrong rather than unhelpfully honest.
        /// </summary>
        [Fact]
        public async Task Sweep_never_puts_a_carried_over_name_on_a_different_sender()
        {
            const string otherPn = "5511977776666@s.whatsapp.net";

            // Only Ana is on disk. Beto is unknown, so nothing can attribute a name to him.
            var people = new MemoryPersonStore();
            people.SeedDisk(new Person
            {
                Jid = Pn,
                Name = "Ana Souza",
                Phone = "5511988887777",
                Source = PersonSource.Observed
            });

            var chatState = new ChatStateStore(new CountingDispatcher());

            // A second row that Ana *does* own, used as the sweep's completion signal so this
            // test waits on an observable event rather than burning wall-clock on a fixed delay.
            var anaRow = new ChatItem
            {
                JID = "120364@g.us",
                Name = "Trabalho",
                IsGroup = true,
                LastMessageIsFromMe = false,
                LastMessageParticipantJid = Pn,
                LastMessageSenderName = "Ana Souza",
                LastMessageAuthor = "5511988887777: "
            };
            chatState.Chats.Add(anaRow);

            var group = new ChatItem
            {
                JID = Group,
                Name = "Família",
                IsGroup = true,
                LastMessageIsFromMe = false,

                // Tip moved to Beto; Carry kept Ana's name beside Beto's digits.
                LastMessageParticipantJid = otherPn,
                LastMessageSenderName = "Ana Souza",
                LastMessageAuthor = "5511977776666: "
            };
            chatState.Chats.Add(group);

            var projection = new ChatAuthorProjection(
                chatState,
                people,
                new AliasJids(),
                strings: null,
                new CountingDispatcher());

            projection.Start();

            // The sweep has demonstrably reached these rows once Ana's own row is repaired.
            await WaitForAsync(() => anaRow.LastMessageAuthor == "Ana Souza: ");

            // Beto's row was visited in the same pass and must have been left alone.
            Assert.Equal("5511977776666: ", group.LastMessageAuthor);
            Assert.NotEqual("Ana Souza: ", group.LastMessageAuthor);
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

            public string PeekCanonicalJid(string jid) => GetCanonicalJid(jid);

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

            public Task UpsertGroupMembershipsAsync(
                string groupJid,
                IReadOnlyList<PersonGroupMembership> members) =>
                Task.CompletedTask;

            public Task RemoveGroupMembershipsAsync(
                string groupJid,
                IReadOnlyList<string> personJids) =>
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
