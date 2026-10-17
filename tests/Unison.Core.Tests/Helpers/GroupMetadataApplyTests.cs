using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class GroupMetadataApplyTests
    {
        private const string Group = "120363000000000001@g.us";
        private static readonly DateTime Now = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);

        // ---- subjects -------------------------------------------------------

        [Fact]
        public void TryCacheSyncedSubject_caches_a_real_subject()
        {
            var f = new Fixture();

            Assert.True(f.Apply.TryCacheSyncedSubject(Group, "Família", null));

            Assert.Equal("Família", f.Naming.CachedNames[Group]);
        }

        [Fact]
        public void TryCacheSyncedSubject_refuses_blank_and_id_placeholders()
        {
            var f = new Fixture();

            Assert.False(f.Apply.TryCacheSyncedSubject(Group, " ", null));
            Assert.False(f.Apply.TryCacheSyncedSubject(Group, "120363000000000001", null));

            Assert.Empty(f.Naming.CachedNames);
        }

        [Fact]
        public void TryCacheSyncedSubject_keeps_a_good_name_from_a_blacklisted_subject()
        {
            var f = new Fixture();

            Assert.False(f.Apply.TryCacheSyncedSubject(Group, "Invite link", "Família"));
            Assert.True(f.Apply.TryCacheSyncedSubject(Group, "Invite link", null));
        }

        [Fact]
        public void TryCacheSyncedSubject_keeps_a_good_cached_subject_from_a_blacklisted_one()
        {
            var f = new Fixture();
            f.Naming.CachedNames.Set(Group, "Família");

            Assert.False(f.Apply.TryCacheSyncedSubject(Group, "Invite link", null));

            Assert.Equal("Família", f.Naming.CachedNames[Group]);
        }

        [Fact]
        public void TryCacheSyncedSubject_replaces_a_cached_placeholder()
        {
            var f = new Fixture();
            f.Naming.CachedNames.Set(Group, "120363000000000001");

            Assert.True(f.Apply.TryCacheSyncedSubject(Group, "Invite link", null));

            Assert.Equal("Invite link", f.Naming.CachedNames[Group]);
        }

        // ---- listing --------------------------------------------------------

        [Fact]
        public async Task ApplyListing_does_nothing_for_an_empty_listing()
        {
            var f = new Fixture();

            await f.Apply.ApplyListingAsync(new Dictionary<string, GroupMetadataSnapshot>());
            await f.Apply.ApplyListingAsync(null);

            Assert.Equal(0, f.Ui.Passes);
            Assert.Equal(0, f.Catalog.Schedules);
        }

        [Fact]
        public async Task ApplyListing_caches_subjects_and_labels_rows_in_one_pass()
        {
            var f = new Fixture();
            f.Jids.Canonical["alias@g.us"] = Group;
            var chat = f.AddChat("alias@g.us", "120363000000000001");
            var other = f.AddChat("999@g.us", "Outro");

            await f.Apply.ApplyListingAsync(Listing(new GroupMetadataSnapshot
            {
                Jid = Group,
                Subject = "Família",
                AnnounceOnly = true,
                MyRole = GroupParticipantRole.Admin,
                MemberCount = 12
            }));

            Assert.Equal("Família", f.Naming.CachedNames[Group]);
            Assert.Equal(1, f.Ui.Passes);
            Assert.Equal(1, f.Catalog.Schedules);
            Assert.True(chat.IsGroup);
            Assert.True(chat.IsAnnounceOnly);
            Assert.Equal(GroupParticipantRole.Admin, chat.MyGroupRole);
            Assert.Equal(12, chat.GroupMemberCount);
            Assert.Equal("Outro", other.Name);
            Assert.False(other.IsAnnounceOnly);
        }

        [Fact]
        public async Task ApplyListing_renames_the_row_from_the_resolved_label()
        {
            var f = new Fixture();
            var chat = f.AddChat(Group, "120363000000000001");

            await f.Apply.ApplyListingAsync(Listing(new GroupMetadataSnapshot { Jid = Group, Subject = "Família" }));

            // The row takes what the chat list would resolve, which is the subject just cached.
            Assert.Equal("Família", chat.Name);
        }

        [Fact]
        public async Task ApplyListing_keeps_a_good_row_name_against_a_blacklisted_subject()
        {
            var f = new Fixture();
            var chat = f.AddChat(Group, "Família");
            f.Naming.ChatNames[Group] = "Invite link";

            await f.Apply.ApplyListingAsync(Listing(new GroupMetadataSnapshot { Jid = Group, Subject = "Invite link" }));

            Assert.Equal("Família", chat.Name);
        }

        [Fact]
        public async Task ApplyListing_keeps_a_good_name_on_an_alias_row_against_a_blacklisted_subject()
        {
            var f = new Fixture();
            f.Jids.Canonical["alias@g.us"] = Group;
            f.AddChat("alias@g.us", "Família");

            await f.Apply.ApplyListingAsync(Listing(new GroupMetadataSnapshot { Jid = Group, Subject = "Invite link" }));

            Assert.Empty(f.Naming.CachedNames);
        }

        [Fact]
        public async Task ApplyListing_ignores_a_placeholder_subject()
        {
            var f = new Fixture();
            var chat = f.AddChat(Group, "Família");

            await f.Apply.ApplyListingAsync(Listing(new GroupMetadataSnapshot
            {
                Jid = Group,
                Subject = "120363000000000001",
                MemberCount = 0
            }));
            Assert.Empty(f.Naming.CachedNames);
            Assert.Equal("Família", chat.Name);
        }

        [Fact]
        public async Task ApplyListing_never_clears_a_known_member_count()
        {
            var f = new Fixture();
            var chat = f.AddChat(Group, "Família");
            chat.GroupMemberCount = 30;

            await f.Apply.ApplyListingAsync(Listing(new GroupMetadataSnapshot { Jid = Group, MemberCount = 0 }));

            Assert.Equal(30, chat.GroupMemberCount);
        }

        // ---- single-group metadata -----------------------------------------

        [Fact]
        public async Task ApplyMetadata_updates_the_canonical_row_and_saves()
        {
            var f = new Fixture();
            f.Jids.Canonical["alias@g.us"] = Group;
            var chat = f.AddChat(Group, "Família");

            await f.Apply.ApplyMetadataAsync("alias@g.us", new GroupMetadataSnapshot
            {
                AnnounceOnly = true,
                MyRole = GroupParticipantRole.SuperAdmin,
                MemberCount = 7,
                Members = Drafts("111@s.whatsapp.net")
            });

            Assert.True(chat.IsGroup);
            Assert.True(chat.IsAnnounceOnly);
            Assert.Equal(GroupParticipantRole.SuperAdmin, chat.MyGroupRole);
            Assert.Equal(7, chat.GroupMemberCount);
            Assert.Single(chat.GroupMembers);
            Assert.Equal(1, f.Catalog.Schedules);
        }

        [Fact]
        public async Task ApplyMetadata_without_a_row_saves_nothing()
        {
            var f = new Fixture();

            await f.Apply.ApplyMetadataAsync(Group, new GroupMetadataSnapshot { AnnounceOnly = true });
            await f.Apply.ApplyMetadataAsync(Group, null);
            await f.Apply.ApplyMetadataAsync(" ", new GroupMetadataSnapshot());

            Assert.Equal(0, f.Catalog.Schedules);
        }

        // ---- roster ---------------------------------------------------------

        [Fact]
        public void ApplyMembers_builds_a_new_roster_with_names_and_known_avatars()
        {
            var f = new Fixture();
            var chat = f.AddChat(Group, "Família");
            f.Naming.MemberNames["111@s.whatsapp.net"] = "Ana";
            f.AddChat("222@s.whatsapp.net", "Bia").AvatarUrl = "b.jpg";
            var drafts = new List<GroupMemberDraft>
            {
                new GroupMemberDraft { Jid = "111@s.whatsapp.net", PhoneNumber = " ", Lid = "999@lid", Role = GroupParticipantRole.Admin },
                new GroupMemberDraft { Jid = "222@s.whatsapp.net" },
                new GroupMemberDraft { Jid = " " },
                null
            };

            f.Apply.ApplyMembers(chat, drafts);

            Assert.Equal(2, chat.GroupMembers.Count);
            GroupMember ana = chat.GroupMembers[0];
            Assert.Equal("Ana", ana.DisplayName);
            Assert.Null(ana.PhoneNumber);
            Assert.Equal("999@lid", ana.Lid);
            Assert.Equal(GroupParticipantRole.Admin, ana.Role);
            Assert.Null(ana.AvatarUrl);
            Assert.Null(ana.AvatarFetchedAtUtc);
            GroupMember bia = chat.GroupMembers[1];
            Assert.Equal("b.jpg", bia.AvatarUrl);
            Assert.Equal(Now, bia.AvatarFetchedAtUtc);
            Assert.Equal(2, chat.GroupMemberCount);
            Assert.Equal(new[] { Group }, f.Announced);
        }

        [Fact]
        public void ApplyMembers_saves_a_detached_roster_and_the_membership_rows()
        {
            var f = new Fixture();
            var chat = f.AddChat(Group, "Família");

            f.Apply.ApplyMembers(chat, Drafts("111@s.whatsapp.net"));

            var roster = Assert.Single(f.Rosters.Replaced);
            Assert.Equal(Group, roster.Key);
            Assert.NotSame(chat.GroupMembers[0], roster.Value[0]);
            var memberships = Assert.Single(f.People.Memberships);
            Assert.Equal(new[] { "111@s.whatsapp.net" }, memberships.Value.Select(r => r.PersonJid).ToArray());
        }

        [Fact]
        public void ApplyMembers_carries_the_lookup_history_of_the_member_it_already_was()
        {
            // A prior member with no picture still falls through to the 1:1 rows: the roster
            // may have been stored before that contact's avatar was cached.
            var f = new Fixture();
            f.Jids.Canonical["999@lid"] = "111@s.whatsapp.net";
            var failedAt = Now.AddHours(-1);
            var chat = f.AddChat(Group, "Família");
            chat.GroupMembers = new List<GroupMember>
            {
                new GroupMember
                {
                    Jid = "111@s.whatsapp.net",
                    AvatarFetchFailedAtUtc = failedAt,
                    AvatarFetchFailureReason = "timeout"
                }
            };
            f.AddChat("999@lid", "Ana").AvatarUrl = "a.jpg";

            f.Apply.ApplyMembers(chat, Drafts("999@lid", "222@s.whatsapp.net"));

            GroupMember moved = chat.GroupMembers[0];
            Assert.Equal("999@lid", moved.Jid);
            Assert.Equal("a.jpg", moved.AvatarUrl);
            Assert.Equal(Now, moved.AvatarFetchedAtUtc);
            Assert.Equal(failedAt, moved.AvatarFetchFailedAtUtc);
            Assert.Equal("timeout", moved.AvatarFetchFailureReason);
        }

        [Fact]
        public void ApplyMembers_merges_onto_the_same_instances_when_nobody_joined_or_left()
        {
            var f = new Fixture();
            var chat = f.AddChat(Group, "Família");
            var ana = new GroupMember { Jid = "111@s.whatsapp.net", DisplayName = "111", AvatarUrl = "a.jpg" };
            var list = new List<GroupMember> { ana };
            chat.GroupMembers = list;
            chat.GroupMemberCount = 0;
            f.Naming.MemberNames["111@s.whatsapp.net"] = "Ana";

            f.Apply.ApplyMembers(chat, new List<GroupMemberDraft>
            {
                new GroupMemberDraft { Jid = "111@s.whatsapp.net", Role = GroupParticipantRole.Admin }
            });

            Assert.Same(list, chat.GroupMembers);
            Assert.Same(ana, chat.GroupMembers[0]);
            Assert.Equal("Ana", ana.DisplayName);
            Assert.Equal(GroupParticipantRole.Admin, ana.Role);
            Assert.Equal("a.jpg", ana.AvatarUrl);
            Assert.Equal(1, chat.GroupMemberCount);
            Assert.Single(f.Rosters.Replaced);
            Assert.Equal(new[] { Group }, f.Announced);
        }

        [Fact]
        public void ApplyMembers_ignores_an_empty_reply()
        {
            var f = new Fixture();
            var existing = new List<GroupMember> { new GroupMember { Jid = "111@s.whatsapp.net" } };
            var chat = f.AddChat(Group, "Família");
            chat.GroupMembers = existing;

            f.Apply.ApplyMembers(chat, new List<GroupMemberDraft>());
            f.Apply.ApplyMembers(chat, null);
            f.Apply.ApplyMembers(chat, new List<GroupMemberDraft> { new GroupMemberDraft { Jid = "" } });

            Assert.Same(existing, chat.GroupMembers);
            Assert.Empty(f.Rosters.Replaced);
            Assert.Empty(f.Announced);
        }

        private static IReadOnlyDictionary<string, GroupMetadataSnapshot> Listing(params GroupMetadataSnapshot[] entries)
        {
            return entries.ToDictionary(e => e.Jid, StringComparer.OrdinalIgnoreCase);
        }

        private static List<GroupMemberDraft> Drafts(params string[] jids)
        {
            return jids.Select(j => new GroupMemberDraft { Jid = j }).ToList();
        }

        private sealed class Fixture
        {
            public Fixture()
            {
                Chats = new ChatStateStore(new InlineDispatcher());
                Chats.GroupRosterChanged += (s, jid) => Announced.Add(jid);
                Rows = new ChatRowIndex(() => Chats.Chats, Jids.GetCanonicalJid);
                Apply = new GroupMetadataApply(
                    Naming, Ui, Catalog, new GroupRosterWriter(Rosters, People), Jids, Chats, Rows, Naming.CachedNames, () => Now);
            }

            public FakeGroupNaming Naming { get; } = new FakeGroupNaming();

            public CountingDispatcher Ui { get; } = new CountingDispatcher();

            public FakeCatalog Catalog { get; } = new FakeCatalog();

            public RecordingRosterStore Rosters { get; } = new RecordingRosterStore();

            public RecordingPersonStore People { get; } = new RecordingPersonStore();

            public FakeJids Jids { get; } = new FakeJids();

            public ChatStateStore Chats { get; }


            public ChatRowIndex Rows { get; }

            public GroupMetadataApply Apply { get; }

            public List<string> Announced { get; } = new List<string>();

            public ChatItem AddChat(string jid, string name)
            {
                var chat = new ChatItem { JID = jid, Name = name };
                Chats.Chats.Add(chat);
                return chat;
            }
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
                string key = JidHelper.Normalize(jid);
                string value;
                return key != null && Canonical.TryGetValue(key, out value) ? value : key;
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
