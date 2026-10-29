// =============================================================================
// GroupRosterDemand — members land one by one, the row follows in batched flushes.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class GroupRosterDemandTests
    {
        private const string Group = "120363000000000009@g.us";
        private const string Ana = "5511900000001@s.whatsapp.net";
        private const string Bia = "5511900000002@s.whatsapp.net";
        private static readonly DateTime T0 = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public async Task An_incoming_author_reaches_the_row_as_a_partial_roster()
        {
            var f = new Fixture();
            ChatItem chat = f.AddGroup();

            f.Demand.NoteMember(Group, Hint(Ana, "Ana"));
            await f.Demand.FlushNowAsync(Group);

            GroupMember ana = Assert.Single(chat.GroupMembers);
            Assert.Equal("Ana", ana.DisplayName);
            Assert.Equal(GroupRosterCompleteness.Partial, chat.RosterCompleteness);
            Assert.False(chat.HasFullGroupRoster);
            Assert.Equal(Ana, f.Announced.Single().ChangedJids.Single());
        }

        [Fact]
        public async Task A_known_member_is_updated_in_place_without_replacing_the_list()
        {
            var f = new Fixture();
            ChatItem chat = f.AddGroup();
            var existing = new GroupMember { Jid = Ana, DisplayName = "Old" };
            chat.GroupMembers = new List<GroupMember> { existing };
            List<GroupMember> list = chat.GroupMembers;

            f.Demand.NoteMember(Group, Hint(Ana, "Ana", GroupParticipantRole.Admin));
            await f.Demand.FlushNowAsync(Group);

            Assert.Same(list, chat.GroupMembers);
            Assert.Same(existing, chat.GroupMembers[0]);
            Assert.Equal("Ana", existing.DisplayName);
            Assert.Equal(GroupParticipantRole.Admin, existing.Role);
        }

        [Fact]
        public async Task A_new_member_keeps_the_members_the_catalog_already_had()
        {
            var f = new Fixture();
            ChatItem chat = f.AddGroup();
            var existing = new GroupMember { Jid = Ana, DisplayName = "Ana" };
            chat.GroupMembers = new List<GroupMember> { existing };

            f.Demand.NoteMember(Group, Hint(Bia, "Bia"));
            await f.Demand.FlushNowAsync(Group);

            Assert.Equal(2, chat.GroupMembers.Count);
            Assert.Same(existing, chat.GroupMembers[0]);
        }

        [Fact]
        public async Task A_leave_drops_the_member_from_the_row_and_the_store()
        {
            var f = new Fixture();
            ChatItem chat = f.AddGroup();
            f.Demand.NoteMember(Group, Hint(Ana, "Ana"));
            f.Demand.NoteMember(Group, Hint(Bia, "Bia"));
            await f.Demand.FlushNowAsync(Group);

            f.Demand.RemoveMember(Group, Bia, T0.AddMinutes(1));
            await f.Demand.FlushNowAsync(Group);

            Assert.Equal(Ana, Assert.Single(chat.GroupMembers).Jid);
            Assert.Contains(f.Rosters.Removed, r => r.Value.Contains(Bia));
        }

        [Fact]
        public async Task Flushes_write_only_the_delta()
        {
            var f = new Fixture();
            f.AddGroup();

            f.Demand.NoteMember(Group, Hint(Ana, "Ana"));
            await f.Demand.FlushNowAsync(Group);

            Assert.Empty(f.Rosters.Replaced);
            Assert.Equal(Ana, Assert.Single(Assert.Single(f.Rosters.Upserted).Value).Jid);
        }

        [Fact]
        public async Task Large_bursts_reach_the_row_in_batches()
        {
            var f = new Fixture();
            ChatItem chat = f.AddGroup();
            for (int i = 0; i < 40; i++)
            {
                f.Demand.NoteMember(Group, Hint("55119000001" + i.ToString("00") + "@s.whatsapp.net", "M" + i));
            }

            int before = f.Ui.Passes;
            await f.Demand.FlushNowAsync(Group);

            Assert.Equal(40, chat.GroupMembers.Count);
            // 40 members / 16 per hop = 3 hops, plus the seed and the commit hop.
            Assert.True(f.Ui.Passes - before <= 5, "hops=" + (f.Ui.Passes - before));
        }

        [Fact]
        public async Task Viewport_authors_come_from_the_store_by_key_with_their_avatar()
        {
            var f = new Fixture();
            ChatItem chat = f.AddGroup();
            f.Rosters.Stored[Ana] = new GroupMember
            {
                Jid = Ana,
                DisplayName = "Ana",
                Role = GroupParticipantRole.Admin,
                AvatarUrl = "ms-appdata:///local/ana.jpg"
            };

            await f.Demand.EnsureMembersAsync(Group, new[] { Ana, Bia });
            await f.Demand.FlushNowAsync(Group);

            GroupMember ana = chat.GroupMembers.Single(m => m.Jid == Ana);
            Assert.Equal(GroupParticipantRole.Admin, ana.Role);
            Assert.Equal("ms-appdata:///local/ana.jpg", ana.AvatarUrl);
            Assert.Contains(chat.GroupMembers, m => m.Jid == Bia);
        }

        [Fact]
        public async Task Ensure_full_asks_the_wire_once_and_reuses_the_answer()
        {
            var f = new Fixture();
            f.AddGroup();
            int fetches = 0;
            var release = new TaskCompletionSource<bool>();
            f.Demand.AttachFullFetch(async (jid, token) =>
            {
                Interlocked.Increment(ref fetches);
                return await release.Task;
            });

            Task first = f.Demand.EnsureFullRosterAsync(Group, "mention");
            Task second = f.Demand.EnsureFullRosterAsync(Group, "members");
            release.SetResult(true);
            await Task.WhenAll(first, second);
            await f.Demand.EnsureFullRosterAsync(Group, "mention");

            Assert.Equal(1, fetches);
        }

        [Fact]
        public async Task Ensure_full_does_not_ask_when_the_roster_is_already_full()
        {
            var f = new Fixture();
            f.AddGroup();
            f.Index.MergeAll(Group, new[] { Hint(Ana, "Ana") }, expectedCount: 1, snapshotUtc: T0);
            bool asked = false;
            f.Demand.AttachFullFetch((jid, token) =>
            {
                asked = true;
                return Task.FromResult(true);
            });

            await f.Demand.EnsureFullRosterAsync(Group, "mention");

            Assert.False(asked);
        }

        [Fact]
        public void TryGetMember_answers_from_memory_before_any_flush()
        {
            var f = new Fixture();
            f.AddGroup();

            f.Demand.NoteMember(Group, Hint(Ana, "Ana"));

            Assert.Equal("Ana", f.Demand.TryGetMember(Group, Ana).DisplayName);
            Assert.Null(f.Demand.TryGetMember(Group, Bia));
        }

        private static GroupMemberHint Hint(string jid, string name, GroupParticipantRole? role = null)
        {
            return new GroupMemberHint
            {
                Jid = jid,
                DisplayName = name,
                Role = role,
                Source = role.HasValue ? GroupMemberHintSource.RoleChange : GroupMemberHintSource.Message,
                ObservedAtUtc = T0
            };
        }

        private sealed class Fixture
        {
            public Fixture()
            {
                Chats = new ChatStateStore(Ui);
                Chats.GroupRosterChanged += (s, e) => Announced.Add(e);
                Index = new GroupRosterIndex(jid => jid);
                Demand = new GroupRosterDemand(
                    Index,
                    Ui,
                    new IdentityJids(),
                    Chats,
                    new ChatRowIndex(() => Chats.Chats, jid => jid),
                    new FakeGroupNaming(),
                    Rosters,
                    new GroupRosterWriter(Rosters, People),
                    flushDebounce: TimeSpan.FromMinutes(5),
                    utcNow: () => T0);
            }

            public CountingDispatcher Ui { get; } = new CountingDispatcher();

            public ChatStateStore Chats { get; }

            public GroupRosterIndex Index { get; }

            public GroupRosterDemand Demand { get; }

            public RecordingRosterStore Rosters { get; } = new RecordingRosterStore();

            public RecordingPersonStore People { get; } = new RecordingPersonStore();

            public List<GroupRosterChangedEventArgs> Announced { get; } = new List<GroupRosterChangedEventArgs>();

            public ChatItem AddGroup()
            {
                var chat = new ChatItem { JID = Group, IsGroup = true };
                Chats.Chats.Add(chat);
                return chat;
            }
        }

        private sealed class IdentityJids : IJidResolver
        {
            public Profile Self => null;

            public string GetCanonicalJid(string jid) => jid;

            public bool TryGetAlias(string jid, out string alias)
            {
                alias = null;
                return false;
            }

            public bool IsSelfLinked(string jid) => false;
        }
    }
}
