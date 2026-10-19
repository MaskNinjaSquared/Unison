using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class GroupRosterRestoreTests
    {
        private const string Group = "1203@g.us";

        [Fact]
        public async Task Fills_the_canonical_row_from_the_store_and_announces_it()
        {
            var fixture = new Fixture();
            fixture.Jids.Canonical["1203-alias@g.us"] = Group;
            var chat = fixture.AddChat(Group);
            fixture.Store.Rows[Group] = new List<GroupMember>
            {
                new GroupMember { Jid = "111@s.whatsapp.net", DisplayName = "Ana" },
                new GroupMember { Jid = "222@s.whatsapp.net" }
            };
            fixture.Naming.MemberNames["222@s.whatsapp.net"] = "Bia";

            await fixture.Restore.EnsureLoadedAsync("1203-alias@g.us");

            Assert.Equal(2, chat.GroupMembers.Count);
            Assert.Equal("Bia", chat.GroupMembers[1].DisplayName);
            Assert.Equal(2, chat.GroupMemberCount);
            Assert.Equal(new[] { Group }, fixture.Announced);
            Assert.Equal(new[] { Group }, fixture.Store.Asked);
            Assert.True(fixture.Store.Initialized);
            Assert.Equal(2, fixture.Ui.Passes);
        }

        [Fact]
        public async Task Ignores_jids_that_are_not_groups()
        {
            var fixture = new Fixture();
            fixture.AddChat("111@s.whatsapp.net");

            await fixture.Restore.EnsureLoadedAsync("111@s.whatsapp.net");
            await fixture.Restore.EnsureLoadedAsync(" ");
            await fixture.Restore.EnsureLoadedAsync(null);

            Assert.Empty(fixture.Store.Asked);
            Assert.Equal(0, fixture.Ui.Passes);
        }

        [Fact]
        public async Task Skips_the_store_when_the_row_is_missing_or_already_has_members()
        {
            var fixture = new Fixture();
            await fixture.Restore.EnsureLoadedAsync(Group);

            fixture.AddChat(Group).GroupMembers = new List<GroupMember> { new GroupMember { Jid = "333@s.whatsapp.net" } };
            await fixture.Restore.EnsureLoadedAsync(Group);

            Assert.Empty(fixture.Store.Asked);
            Assert.Empty(fixture.Announced);
        }

        [Fact]
        public async Task Leaves_the_row_alone_when_the_store_is_empty_or_fails()
        {
            var fixture = new Fixture();
            var chat = fixture.AddChat(Group);

            await fixture.Restore.EnsureLoadedAsync(Group);
            fixture.Store.Throw = true;
            await fixture.Restore.EnsureLoadedAsync(Group);

            Assert.False(chat.HasGroupMembers);
            Assert.Empty(fixture.Announced);
            Assert.Equal(2, fixture.Ui.Passes);
        }

        [Fact]
        public async Task Does_not_overwrite_a_roster_that_landed_during_the_read()
        {
            var fixture = new Fixture();
            var chat = fixture.AddChat(Group);
            var live = new List<GroupMember> { new GroupMember { Jid = "333@s.whatsapp.net" } };
            fixture.Store.Rows[Group] = new List<GroupMember> { new GroupMember { Jid = "111@s.whatsapp.net" } };
            fixture.Store.OnRead = () => chat.GroupMembers = live;

            await fixture.Restore.EnsureLoadedAsync(Group);

            Assert.Same(live, chat.GroupMembers);
            Assert.Empty(fixture.Announced);
        }

        private sealed class Fixture
        {
            public Fixture()
            {
                Chats = new ChatStateStore(new InlineDispatcher());
                Chats.GroupRosterChanged += (s, jid) => Announced.Add(jid);
                Restore = new GroupRosterRestore(Naming, Ui, Jids, Chats, Store);
            }

            public FakeGroupNaming Naming { get; } = new FakeGroupNaming();

            public CountingDispatcher Ui { get; } = new CountingDispatcher();

            public FakeJids Jids { get; } = new FakeJids();

            public FakeStore Store { get; } = new FakeStore();

            public ChatStateStore Chats { get; }

            public GroupRosterRestore Restore { get; }

            public List<string> Announced { get; } = new List<string>();

            public ChatItem AddChat(string jid)
            {
                var chat = new ChatItem { JID = jid };
                Chats.Chats.Add(chat);
                return chat;
            }
        }

        private sealed class FakeStore : IGroupRosterStore
        {
            public Dictionary<string, IReadOnlyList<GroupMember>> Rows { get; } =
                new Dictionary<string, IReadOnlyList<GroupMember>>(StringComparer.OrdinalIgnoreCase);

            public List<string> Asked { get; } = new List<string>();

            public bool Initialized { get; private set; }

            public bool Throw { get; set; }

            public Action OnRead { get; set; }

            public Task InitializeAsync()
            {
                Initialized = true;
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<GroupMember>> GetForGroupAsync(string groupJid)
            {
                Asked.Add(groupJid);
                if (Throw)
                {
                    throw new InvalidOperationException("disk");
                }

                OnRead?.Invoke();
                IReadOnlyList<GroupMember> rows;
                return Task.FromResult(Rows.TryGetValue(groupJid, out rows) ? rows : (IReadOnlyList<GroupMember>)new GroupMember[0]);
            }

            public Task ReplaceForGroupAsync(string groupJid, IReadOnlyList<GroupMember> members) =>
                Task.CompletedTask;

            public Task ClearAllAsync() => Task.CompletedTask;
        }

        private sealed class InlineDispatcher : IDispatcher
        {
            public Task RunAsync(Action action)
            {
                action();
                return Task.CompletedTask;
            }
        }

        private sealed class FakeJids : IJidResolver
        {
            public Dictionary<string, string> Canonical { get; } =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public Profile Self => null;

            public string GetCanonicalJid(string jid)
            {
                string value;
                return jid != null && Canonical.TryGetValue(jid, out value) ? value : jid;
            }

            public bool TryGetAlias(string jid, out string alias)
            {
                alias = null;
                return false;
            }

            public bool IsSelfLinked(string jid) => false;
        }
    }
}
