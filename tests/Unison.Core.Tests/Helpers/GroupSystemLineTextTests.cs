// =============================================================================
// Tests for GroupSystemLineText.
// =============================================================================
using Unison.Core.Helpers.Groups;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class GroupSystemLineTextTests
    {
        [Fact]
        public void Join_without_distinct_actor_uses_join_template()
        {
            string text = GroupSystemLineText.JoinOrAdded(
                "{0} joined the group",
                "{0} added {1}",
                "Fulano",
                "Beltrano",
                null,
                "fulano@s.whatsapp.net");

            Assert.Equal("Fulano joined the group", text);
        }

        [Fact]
        public void Join_with_distinct_actor_uses_added_by()
        {
            string text = GroupSystemLineText.JoinOrAdded(
                "{0} joined the group",
                "{0} added {1}",
                "Fulano",
                "Beltrano",
                "beltrano@s.whatsapp.net",
                "fulano@s.whatsapp.net");

            Assert.Equal("Beltrano added Fulano", text);
        }

        [Fact]
        public void Leave_same_actor_is_left_wording()
        {
            string text = GroupSystemLineText.LeaveOrRemoved(
                "{0} left",
                "{0} removed {1}",
                "Fulano",
                "Fulano",
                "fulano@s.whatsapp.net",
                "fulano@s.whatsapp.net");

            Assert.Equal("Fulano left", text);
        }

        [Fact]
        public void Leave_distinct_actor_is_removed_by()
        {
            string text = GroupSystemLineText.LeaveOrRemoved(
                "{0} left",
                "{0} removed {1}",
                "Fulano",
                "Beltrano",
                "beltrano@s.whatsapp.net",
                "fulano@s.whatsapp.net");

            Assert.Equal("Beltrano removed Fulano", text);
        }

        [Fact]
        public void Leave_same_person_pn_vs_lid_is_left_not_removed()
        {
            // Wire often addresses the leaver as author PN and child LID (or the reverse).
            string text = GroupSystemLineText.LeaveOrRemoved(
                "{0} left",
                "{0} removed {1}",
                "Fulano",
                "Fulano",
                "5511999888777@s.whatsapp.net",
                "1234567890@lid",
                jid => jid != null && jid.IndexOf("@lid", System.StringComparison.OrdinalIgnoreCase) >= 0
                    ? "5511999888777@s.whatsapp.net"
                    : jid);

            Assert.Equal("Fulano left", text);
        }

        [Fact]
        public void Leave_same_phone_digits_is_left_not_removed()
        {
            string text = GroupSystemLineText.LeaveOrRemoved(
                "{0} left",
                "{0} removed {1}",
                "Fulano",
                "Fulano",
                "1234567890@lid",
                "999@lid",
                canonicalOf: null,
                actorPhoneDigits: "5511999",
                participantPhoneDigits: "5511999");

            Assert.Equal("Fulano left", text);
        }

        [Fact]
        public void HasDistinctActor_false_when_canonical_collapses_forms()
        {
            Assert.False(GroupSystemLineText.HasDistinctActor(
                "123@lid",
                "5511@s.whatsapp.net",
                jid => "5511@s.whatsapp.net"));
        }

        [Fact]
        public void Subject_with_actor_and_previous()
        {
            string text = GroupSystemLineText.SubjectChanged(
                "{0} changed the group name from \"{1}\" to \"{2}\"",
                "{0} changed the group name to \"{1}\"",
                "Beltrano",
                "Old",
                "New");

            Assert.Equal("Beltrano changed the group name from \"Old\" to \"New\"", text);
        }

        [Fact]
        public void Subject_anonymous_to_only()
        {
            string text = GroupSystemLineText.SubjectChanged(
                "{0} changed the group name from \"{1}\" to \"{2}\"",
                "{0} changed the group name to \"{1}\"",
                null,
                null,
                "New",
                "Group name changed from \"{0}\" to \"{1}\"",
                "Group name changed to \"{0}\"");

            Assert.Equal("Group name changed to \"New\"", text);
        }
    }
}
