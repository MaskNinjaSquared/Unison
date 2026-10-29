using System;
using System.Collections.Generic;
using System.Linq;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.WhatsAppClient.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class GroupRosterRulesTests
    {
        [Fact]
        public void CopyForStore_returns_null_for_empty_and_skips_null_rows()
        {
            Assert.Null(GroupRosterRules.CopyForStore(null));
            Assert.Null(GroupRosterRules.CopyForStore(new List<GroupMember>()));
            Assert.Null(GroupRosterRules.CopyForStore(new List<GroupMember> { null }));
        }

        [Fact]
        public void CopyForStore_copies_every_field_into_new_instances()
        {
            var fetched = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var failed = fetched.AddMinutes(1);
            var source = new GroupMember
            {
                Jid = "111@s.whatsapp.net",
                PhoneNumber = "111",
                Lid = "999@lid",
                DisplayName = "Ana",
                Role = GroupParticipantRole.Admin,
                AvatarUrl = "ms-appdata:///local/a.jpg",
                AvatarFetchedAtUtc = fetched,
                AvatarFetchFailedAtUtc = failed,
                AvatarFetchFailureReason = "timeout"
            };

            List<GroupMember> copy = GroupRosterRules.CopyForStore(new List<GroupMember> { source });

            GroupMember row = Assert.Single(copy);
            Assert.NotSame(source, row);
            Assert.Equal("111@s.whatsapp.net", row.Jid);
            Assert.Equal("111", row.PhoneNumber);
            Assert.Equal("999@lid", row.Lid);
            Assert.Equal("Ana", row.DisplayName);
            Assert.Equal(GroupParticipantRole.Admin, row.Role);
            Assert.Equal("ms-appdata:///local/a.jpg", row.AvatarUrl);
            Assert.Equal(fetched, row.AvatarFetchedAtUtc);
            Assert.Equal(failed, row.AvatarFetchFailedAtUtc);
            Assert.Equal("timeout", row.AvatarFetchFailureReason);
        }

        [Fact]
        public void MembershipPersonKeys_covers_jid_lid_phone_and_their_canonicals()
        {
            var jids = new FakeJids();
            jids.Canonical["999@lid"] = "111@s.whatsapp.net";
            var member = new GroupMember
            {
                Jid = "999:3@lid",
                Lid = "999@lid",
                PhoneNumber = "+55 (11) 111"
            };

            IReadOnlyList<string> keys = GroupRosterRules.MembershipPersonKeys(member, jids);

            Assert.Equal(
                new[] { "999@lid", "111@s.whatsapp.net", "5511111@s.whatsapp.net" },
                keys);
        }

        [Fact]
        public void MembershipPersonKeys_keeps_a_phone_that_is_already_a_jid_and_drops_groups()
        {
            var member = new GroupMember
            {
                Jid = "1203@g.us",
                PhoneNumber = "111@s.whatsapp.net"
            };

            IReadOnlyList<string> keys = GroupRosterRules.MembershipPersonKeys(member, new FakeJids());

            Assert.Equal(new[] { "111@s.whatsapp.net" }, keys);
        }

        [Fact]
        public void MembershipPersonKeys_without_a_resolver_lists_the_raw_addresses()
        {
            // The info pane is built without a resolver when no participant context was given.
            // It still has to ask for the addresses the member actually carries.
            var member = new GroupMember
            {
                Jid = "999:3@lid",
                Lid = "999@lid",
                PhoneNumber = "+55 (11) 111"
            };

            IReadOnlyList<string> keys = GroupRosterRules.MembershipPersonKeys(member, null);

            Assert.Equal(new[] { "999@lid", "5511111@s.whatsapp.net" }, keys);
        }

        [Fact]
        public void MemberMatches_without_a_resolver_compares_the_normalized_addresses()
        {
            Assert.True(GroupRosterRules.MemberMatches(
                new GroupMember { Jid = "111:2@s.whatsapp.net" }, "111@s.whatsapp.net", null));
            Assert.True(GroupRosterRules.MemberMatches(
                new GroupMember { PhoneNumber = "111@s.whatsapp.net" }, "111@s.whatsapp.net", null));
            Assert.False(GroupRosterRules.MemberMatches(
                new GroupMember { Lid = "999@lid" }, "111@s.whatsapp.net", null));
        }

        [Fact]
        public void Memberships_writes_one_row_per_person_key_without_duplicates()
        {
            var jids = new FakeJids();
            jids.Canonical["999@lid"] = "111@s.whatsapp.net";
            var members = new List<GroupMember>
            {
                null,
                new GroupMember { Jid = "999@lid", Role = GroupParticipantRole.Admin },
                new GroupMember { Jid = "111@s.whatsapp.net" }
            };

            List<PersonGroupMembership> rows = GroupRosterRules.Memberships("1203@g.us", members, jids);

            Assert.Equal(
                new[] { "999@lid", "111@s.whatsapp.net" },
                rows.Select(r => r.PersonJid).ToArray());
            Assert.All(rows, r => Assert.Equal("1203@g.us", r.GroupJid));
            Assert.All(rows, r => Assert.Equal(GroupParticipantRole.Admin, r.Role));
        }

        [Fact]
        public void MemberMatches_compares_jid_phone_and_lid_by_canonical()
        {
            var jids = new FakeJids();
            jids.Canonical["999@lid"] = "111@s.whatsapp.net";

            Assert.True(GroupRosterRules.MemberMatches(
                new GroupMember { Jid = "999:2@lid" }, "111@s.whatsapp.net", jids));
            Assert.True(GroupRosterRules.MemberMatches(
                new GroupMember { Jid = "x@lid", PhoneNumber = "111@s.whatsapp.net" }, "111@s.whatsapp.net", jids));
            Assert.True(GroupRosterRules.MemberMatches(
                new GroupMember { Jid = "x@lid", Lid = "999@lid" }, "111@s.whatsapp.net", jids));
            Assert.False(GroupRosterRules.MemberMatches(
                new GroupMember { Jid = "222@s.whatsapp.net" }, "111@s.whatsapp.net", jids));
            Assert.False(GroupRosterRules.MemberMatches(null, "111@s.whatsapp.net", jids));
            Assert.False(GroupRosterRules.MemberMatches(new GroupMember { Jid = "111@s.whatsapp.net" }, " ", jids));
        }

        [Fact]
        public void FindPrevious_prefers_the_exact_jid()
        {
            var exact = new GroupMember { Jid = "999@lid" };
            var previous = Previous(exact);

            GroupMember found = GroupRosterRules.FindPrevious(
                ByJid(previous), previous, "999@lid", null, null, new FakeJids());

            Assert.Same(exact, found);
        }

        [Fact]
        public void FindPrevious_matches_across_the_lid_and_pn_halves()
        {
            var jids = new FakeJids();
            jids.Canonical["999@lid"] = "111@s.whatsapp.net";
            var stored = new GroupMember { Jid = "111@s.whatsapp.net" };
            var previous = Previous(stored);

            Assert.Same(stored, GroupRosterRules.FindPrevious(
                ByJid(previous), previous, "999@lid", null, null, jids));
            Assert.Same(stored, GroupRosterRules.FindPrevious(
                ByJid(previous), previous, "888@lid", "999@lid", null, jids));
            Assert.Same(stored, GroupRosterRules.FindPrevious(
                ByJid(previous), previous, "888@lid", null, "111", jids));
            Assert.Null(GroupRosterRules.FindPrevious(
                ByJid(previous), previous, "888@lid", null, "222", jids));
        }

        [Fact]
        public void FindPrevious_returns_null_without_a_previous_roster()
        {
            Assert.Null(GroupRosterRules.FindPrevious(null, null, "111@s.whatsapp.net", null, null, new FakeJids()));
        }

        [Fact]
        public void RosterJidSetsEqual_needs_the_same_jids_and_count()
        {
            var previous = ByJid(Previous(
                new GroupMember { Jid = "111@s.whatsapp.net" },
                new GroupMember { Jid = "222@s.whatsapp.net" }));

            Assert.True(GroupRosterRules.RosterJidSetsEqual(previous, new List<GroupMember>
            {
                new GroupMember { Jid = "222@S.WHATSAPP.NET" },
                new GroupMember { Jid = "111@s.whatsapp.net" }
            }));
            Assert.False(GroupRosterRules.RosterJidSetsEqual(previous, new List<GroupMember>
            {
                new GroupMember { Jid = "111@s.whatsapp.net" }
            }));
            Assert.False(GroupRosterRules.RosterJidSetsEqual(previous, new List<GroupMember>
            {
                new GroupMember { Jid = "111@s.whatsapp.net" },
                new GroupMember { Jid = "333@s.whatsapp.net" }
            }));
            Assert.False(GroupRosterRules.RosterJidSetsEqual(null, new List<GroupMember>()));
        }

        [Fact]
        public void MergeInPlace_writes_only_what_the_reply_actually_carried()
        {
            var fetched = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var dest = new GroupMember
            {
                Jid = "111@s.whatsapp.net",
                PhoneNumber = "111",
                Lid = "999@lid",
                DisplayName = "Ana",
                Role = GroupParticipantRole.Admin,
                AvatarUrl = "old.jpg",
                AvatarFetchedAtUtc = fetched,
                AvatarFetchFailureReason = "timeout"
            };
            var existing = new List<GroupMember> { dest };

            GroupRosterRules.MergeInPlace(existing, new List<GroupMember>
            {
                new GroupMember { Jid = "111@s.whatsapp.net", Role = GroupParticipantRole.Member, DisplayName = "Ana Paula" }
            });

            Assert.Same(dest, existing[0]);
            Assert.Equal("111", dest.PhoneNumber);
            Assert.Equal("999@lid", dest.Lid);
            Assert.Equal("Ana Paula", dest.DisplayName);
            Assert.Equal(GroupParticipantRole.Member, dest.Role);
            Assert.Equal("old.jpg", dest.AvatarUrl);
            Assert.Equal(fetched, dest.AvatarFetchedAtUtc);
            Assert.Equal("timeout", dest.AvatarFetchFailureReason);
        }

        private static List<GroupMember> Previous(params GroupMember[] members) => members.ToList();

        private static Dictionary<string, GroupMember> ByJid(List<GroupMember> members) =>
            members.ToDictionary(m => m.Jid, StringComparer.OrdinalIgnoreCase);

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
