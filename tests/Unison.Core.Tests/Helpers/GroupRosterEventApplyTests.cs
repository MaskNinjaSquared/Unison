using System;
using System.Collections.Generic;
using Unison.Core.Models;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class GroupRosterEventApplyTests
    {
        private const string Group = "120363000000000000@g.us";
        private const string Lid = "111@lid";
        private const string Phone = "5511999990000@s.whatsapp.net";
        private static readonly DateTime At = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

        private readonly List<GroupMemberHint> _notes = new List<GroupMemberHint>();
        private readonly List<string> _removed = new List<string>();

        private void Apply(GroupRosterEventKind kind)
        {
            GroupRosterEventApply.Apply(
                new GroupRosterEvent(Group, kind, new[] { new GroupRosterEventParticipant(Lid, Phone) }, At),
                (g, hint) => _notes.Add(hint),
                (g, jid, at) => _removed.Add(jid));
        }

        [Fact]
        public void Add_notes_a_member_with_both_addresses()
        {
            Apply(GroupRosterEventKind.Add);

            GroupMemberHint hint = Assert.Single(_notes);
            Assert.Equal(Lid, hint.Jid);
            Assert.Equal(Phone, hint.AltJid);
            Assert.Equal(GroupParticipantRole.Member, hint.Role);
            Assert.Equal(GroupMemberHintSource.Join, hint.Source);
            Assert.Equal(At, hint.ObservedAtUtc);
        }

        [Fact]
        public void Promote_and_demote_carry_the_role()
        {
            Apply(GroupRosterEventKind.Promote);
            Apply(GroupRosterEventKind.Demote);

            Assert.Equal(GroupParticipantRole.Admin, _notes[0].Role);
            Assert.Equal(GroupParticipantRole.Member, _notes[1].Role);
        }

        [Fact]
        public void Modify_links_the_new_address_without_touching_the_role()
        {
            Apply(GroupRosterEventKind.Modify);

            Assert.Null(Assert.Single(_notes).Role);
        }

        [Fact]
        public void Remove_drops_every_address_and_notes_nothing()
        {
            Apply(GroupRosterEventKind.Remove);

            Assert.Empty(_notes);
            Assert.Equal(new[] { Lid, Phone }, _removed);
        }
    }
}
