// =============================================================================
// Tests for GroupRosterWriter.
// =============================================================================
using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class GroupRosterWriterTests
    {
        private const string Group = "120363000000000000@g.us";

        [Fact]
        public void A_roster_and_its_memberships_reach_their_stores()
        {
            var rosters = new RecordingRosterStore();
            var people = new RecordingPersonStore();
            var writer = new GroupRosterWriter(rosters, people);

            writer.SaveRoster(Group, new List<GroupMember> { new GroupMember { Jid = "111@s.whatsapp.net" } });
            writer.SaveMemberships(Group, new List<PersonGroupMembership>
            {
                new PersonGroupMembership { PersonJid = "111@s.whatsapp.net", GroupJid = Group }
            });

            Assert.Equal(Group, Assert.Single(rosters.Replaced).Key);
            Assert.Equal(Group, Assert.Single(people.Memberships).Key);
        }

        [Fact]
        public void Nothing_to_write_writes_nothing()
        {
            var rosters = new RecordingRosterStore();
            var people = new RecordingPersonStore();
            var writer = new GroupRosterWriter(rosters, people);

            writer.SaveRoster(Group, new List<GroupMember>());
            writer.SaveRoster(Group, null);
            writer.SaveMemberships(Group, new List<PersonGroupMembership>());
            writer.SaveMemberships(Group, null);

            Assert.Empty(rosters.Replaced);
            Assert.Empty(people.Memberships);
        }

        [Fact]
        public void A_failing_store_does_not_reach_the_caller()
        {
            var rosters = new RecordingRosterStore { Throw = true };
            var writer = new GroupRosterWriter(rosters, new RecordingPersonStore());

            writer.SaveRoster(Group, new List<GroupMember> { new GroupMember { Jid = "111@s.whatsapp.net" } });

            Assert.Empty(rosters.Replaced);
        }
    }
}
