using System;
using System.Collections.Generic;
using System.Linq;
using Unison.Core.Models;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State
{
    public class GroupRosterIndexTests
    {
        private const string Group = "120363000000000001@g.us";
        private const string AlicePn = "5511999990001@s.whatsapp.net";
        private const string AliceLid = "11111111111111@lid";
        private const string BobPn = "5511999990002@s.whatsapp.net";

        private static readonly DateTime T0 = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

        private static GroupRosterIndex NewIndex(Dictionary<string, string> aliases = null)
        {
            return new GroupRosterIndex(jid =>
            {
                string mapped;
                return aliases != null && jid != null && aliases.TryGetValue(jid, out mapped) ? mapped : jid;
            });
        }

        private static GroupMemberHint Hint(
            string jid,
            GroupMemberHintSource source,
            DateTime at,
            string name = null,
            GroupParticipantRole? role = null,
            string alt = null)
        {
            return new GroupMemberHint
            {
                Jid = jid,
                AltJid = alt,
                DisplayName = name,
                Role = role,
                Source = source,
                ObservedAtUtc = at
            };
        }

        [Fact]
        public void A_message_author_becomes_a_known_member_and_the_roster_is_partial()
        {
            var index = NewIndex();

            bool changed = index.Note(Group, Hint(AlicePn, GroupMemberHintSource.Message, T0, "Alice"));

            Assert.True(changed);
            GroupRosterRecord alice = index.TryGet(Group, AlicePn);
            Assert.NotNull(alice);
            Assert.Equal("Alice", alice.DisplayName);
            Assert.Equal(GroupRosterCompleteness.Partial, index.GetCompleteness(Group));
        }

        [Fact]
        public void Noting_the_same_member_twice_with_nothing_new_is_not_a_change()
        {
            var index = NewIndex();
            index.Note(Group, Hint(AlicePn, GroupMemberHintSource.Message, T0, "Alice"));
            index.TakePending(Group);

            bool changed = index.Note(Group, Hint(AlicePn, GroupMemberHintSource.Message, T0.AddSeconds(5), "Alice"));

            Assert.False(changed);
            Assert.True(index.TakePending(Group).IsEmpty);
        }

        [Fact]
        public void A_lid_and_its_phone_number_are_one_member()
        {
            var index = NewIndex();
            index.Note(Group, Hint(AliceLid, GroupMemberHintSource.Message, T0, "Alice", alt: AlicePn));

            GroupRosterRecord byPhone = index.TryGet(Group, AlicePn);
            GroupRosterRecord byLid = index.TryGet(Group, AliceLid);

            Assert.NotNull(byPhone);
            Assert.Equal(byPhone.Jid, byLid.Jid);
            Assert.Equal(AliceLid, byPhone.Lid);
            Assert.Equal(AlicePn, byPhone.PhoneNumber);
            Assert.Single(index.Snapshot(Group));
        }

        [Fact]
        public void A_lid_learned_after_the_phone_merges_the_two_entries_and_drops_the_duplicate()
        {
            var index = NewIndex();
            index.Note(Group, Hint(AlicePn, GroupMemberHintSource.Message, T0, "Alice"));
            index.Note(Group, Hint(AliceLid, GroupMemberHintSource.Quote, T0.AddSeconds(1)));
            index.TakePending(Group);

            index.Note(Group, Hint(AliceLid, GroupMemberHintSource.Message, T0.AddSeconds(2), alt: AlicePn));

            Assert.Single(index.Snapshot(Group));
            GroupRosterDelta delta = index.TakePending(Group);
            Assert.Single(delta.Removed);
        }

        [Fact]
        public void The_canonicalizer_decides_the_member_key()
        {
            var index = NewIndex(new Dictionary<string, string> { { AliceLid, AlicePn } });
            index.Note(Group, Hint(AlicePn, GroupMemberHintSource.Message, T0, "Alice"));

            GroupRosterRecord found = index.TryGet(Group, AliceLid);

            Assert.NotNull(found);
            Assert.Equal(AlicePn, found.Jid);
        }

        [Fact]
        public void A_leave_removes_the_member_and_queues_the_removal()
        {
            var index = NewIndex();
            index.Note(Group, Hint(AlicePn, GroupMemberHintSource.Join, T0));
            index.TakePending(Group);

            bool removed = index.Remove(Group, AlicePn, T0.AddMinutes(1));

            Assert.True(removed);
            Assert.Null(index.TryGet(Group, AlicePn));
            GroupRosterDelta delta = index.TakePending(Group);
            Assert.Equal(AlicePn, delta.Removed.Single().Jid);
        }

        [Fact]
        public void A_history_message_older_than_the_leave_does_not_bring_the_member_back()
        {
            var index = NewIndex();
            index.Note(Group, Hint(AlicePn, GroupMemberHintSource.Join, T0));
            index.Remove(Group, AlicePn, T0.AddMinutes(1));

            bool changed = index.Note(Group, Hint(AlicePn, GroupMemberHintSource.Message, T0.AddSeconds(30)));

            Assert.False(changed);
            Assert.Null(index.TryGet(Group, AlicePn));
        }

        [Fact]
        public void A_join_after_the_leave_brings_the_member_back()
        {
            var index = NewIndex();
            index.Remove(Group, AlicePn, T0);

            bool changed = index.Note(Group, Hint(AlicePn, GroupMemberHintSource.Join, T0.AddMinutes(1)));

            Assert.True(changed);
            Assert.NotNull(index.TryGet(Group, AlicePn));
        }

        [Fact]
        public void A_complete_metadata_reply_makes_the_roster_full()
        {
            var index = NewIndex();

            GroupRosterCompleteness result = index.MergeAll(
                Group,
                new[]
                {
                    Hint(AlicePn, GroupMemberHintSource.Metadata, T0, role: GroupParticipantRole.Admin),
                    Hint(BobPn, GroupMemberHintSource.Metadata, T0, role: GroupParticipantRole.Member)
                },
                expectedCount: 2,
                snapshotUtc: T0);

            Assert.Equal(GroupRosterCompleteness.Full, result);
            Assert.Equal(GroupRosterCompleteness.Full, index.GetCompleteness(Group));
            Assert.Equal(GroupParticipantRole.Admin, index.TryGet(Group, AlicePn).Role);
        }

        [Fact]
        public void A_truncated_metadata_reply_stays_partial()
        {
            var index = NewIndex();

            GroupRosterCompleteness result = index.MergeAll(
                Group,
                new[] { Hint(AlicePn, GroupMemberHintSource.Metadata, T0) },
                expectedCount: 600,
                snapshotUtc: T0);

            Assert.Equal(GroupRosterCompleteness.Partial, result);
        }

        [Fact]
        public void A_full_reply_drops_members_the_server_no_longer_lists()
        {
            var index = NewIndex();
            index.Note(Group, Hint(BobPn, GroupMemberHintSource.Message, T0.AddMinutes(-10)));
            index.TakePending(Group);

            index.MergeAll(
                Group,
                new[] { Hint(AlicePn, GroupMemberHintSource.Metadata, T0) },
                expectedCount: 1,
                snapshotUtc: T0);

            Assert.Null(index.TryGet(Group, BobPn));
            Assert.Contains(index.TakePending(Group).Removed, r => r.Jid == BobPn);
        }

        [Fact]
        public void A_join_newer_than_the_metadata_snapshot_survives_a_full_reply()
        {
            var index = NewIndex();
            index.Note(Group, Hint(BobPn, GroupMemberHintSource.Join, T0.AddSeconds(10)));

            index.MergeAll(
                Group,
                new[] { Hint(AlicePn, GroupMemberHintSource.Metadata, T0) },
                expectedCount: 1,
                snapshotUtc: T0);

            Assert.NotNull(index.TryGet(Group, BobPn));
        }

        [Fact]
        public void A_leave_newer_than_the_metadata_snapshot_is_not_undone()
        {
            var index = NewIndex();
            index.Remove(Group, BobPn, T0.AddSeconds(10));

            index.MergeAll(
                Group,
                new[]
                {
                    Hint(AlicePn, GroupMemberHintSource.Metadata, T0),
                    Hint(BobPn, GroupMemberHintSource.Metadata, T0)
                },
                expectedCount: 2,
                snapshotUtc: T0);

            Assert.Null(index.TryGet(Group, BobPn));
        }

        [Fact]
        public void A_promote_newer_than_the_metadata_snapshot_keeps_its_role()
        {
            var index = NewIndex();
            index.Note(Group, Hint(AlicePn, GroupMemberHintSource.RoleChange, T0.AddSeconds(10), role: GroupParticipantRole.Admin));

            index.MergeAll(
                Group,
                new[] { Hint(AlicePn, GroupMemberHintSource.Metadata, T0, role: GroupParticipantRole.Member) },
                expectedCount: 1,
                snapshotUtc: T0);

            Assert.Equal(GroupParticipantRole.Admin, index.TryGet(Group, AlicePn).Role);
        }

        [Fact]
        public void A_full_roster_ignores_older_sightings_of_people_it_did_not_list()
        {
            var index = NewIndex();
            index.MergeAll(
                Group,
                new[] { Hint(AlicePn, GroupMemberHintSource.Metadata, T0) },
                expectedCount: 1,
                snapshotUtc: T0);

            Assert.False(index.Note(Group, Hint(BobPn, GroupMemberHintSource.Quote, DateTime.MinValue)));
            Assert.False(index.Note(Group, Hint(BobPn, GroupMemberHintSource.Message, T0.AddMinutes(-5))));
            Assert.Null(index.TryGet(Group, BobPn));

            Assert.True(index.Note(Group, Hint(BobPn, GroupMemberHintSource.Message, T0.AddMinutes(5))));
            Assert.NotNull(index.TryGet(Group, BobPn));
        }

        [Fact]
        public void A_message_without_a_role_never_demotes_an_admin()
        {
            var index = NewIndex();
            index.Note(Group, Hint(AlicePn, GroupMemberHintSource.RoleChange, T0, role: GroupParticipantRole.Admin));

            index.Note(Group, Hint(AlicePn, GroupMemberHintSource.Message, T0.AddMinutes(1), "Alice"));

            Assert.Equal(GroupParticipantRole.Admin, index.TryGet(Group, AlicePn).Role);
        }

        [Fact]
        public void Seeding_from_the_row_does_not_queue_a_flush()
        {
            var index = NewIndex();

            index.Seed(
                Group,
                new[] { Hint(AlicePn, GroupMemberHintSource.Store, T0, "Alice") },
                GroupRosterCompleteness.Full);

            Assert.True(index.TakePending(Group).IsEmpty);
            Assert.Equal(GroupRosterCompleteness.Full, index.GetCompleteness(Group));
            Assert.NotNull(index.TryGet(Group, AlicePn));
        }

        [Fact]
        public void Pending_changes_are_handed_out_once()
        {
            var index = NewIndex();
            index.Note(Group, Hint(AlicePn, GroupMemberHintSource.Message, T0));
            index.Note(Group, Hint(BobPn, GroupMemberHintSource.Message, T0));

            GroupRosterDelta first = index.TakePending(Group);
            GroupRosterDelta second = index.TakePending(Group);

            Assert.Equal(2, first.Upserted.Count);
            Assert.True(second.IsEmpty);
        }

        [Fact]
        public void Group_members_are_rejected_as_participants()
        {
            var index = NewIndex();

            Assert.False(index.Note(Group, Hint("120363999@g.us", GroupMemberHintSource.Message, T0)));
            Assert.False(index.Note(Group, Hint(null, GroupMemberHintSource.Message, T0)));
            Assert.Equal(GroupRosterCompleteness.None, index.GetCompleteness(Group));
        }
    }
}
