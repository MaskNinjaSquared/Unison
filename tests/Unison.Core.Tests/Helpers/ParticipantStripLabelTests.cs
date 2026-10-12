// =============================================================================
// Tests for ParticipantStripLabel — the group strip's last-resort sender label.
// =============================================================================
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ParticipantStripLabelTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Nothing_known_yields_no_label(string? participantJid)
        {
            Assert.Equal(string.Empty, ParticipantStripLabel.ForParticipant(participantJid));
        }

        [Fact]
        public void Phone_participant_yields_its_digits()
        {
            Assert.Equal(
                "5511988887777",
                ParticipantStripLabel.ForParticipant("5511988887777@s.whatsapp.net"));
        }

        [Fact]
        public void Device_suffix_is_dropped()
        {
            Assert.Equal(
                "5511988887777",
                ParticipantStripLabel.ForParticipant("5511988887777:12@s.whatsapp.net"));
        }

        /// <summary>
        /// LID digits are an internal identity, not a number anyone can be reached at. Showing them
        /// reads as a phone number that belongs to nobody, so the strip stays without an author.
        /// </summary>
        [Fact]
        public void Lid_alone_yields_no_label()
        {
            Assert.Equal(string.Empty, ParticipantStripLabel.ForParticipant("199999999999@lid"));
        }

        [Fact]
        public void Lid_with_a_known_phone_yields_the_phone()
        {
            Assert.Equal(
                "5511988887777",
                ParticipantStripLabel.ForParticipant(
                    "199999999999@lid",
                    "5511988887777@s.whatsapp.net"));
        }

        [Fact]
        public void Canonical_that_is_still_a_lid_yields_no_label()
        {
            Assert.Equal(
                string.Empty,
                ParticipantStripLabel.ForParticipant("199999999999@lid", "199999999999@lid"));
        }

        [Fact]
        public void Group_jid_is_not_a_participant_label()
        {
            Assert.Equal(string.Empty, ParticipantStripLabel.ForParticipant("123456789-987@g.us"));
        }
    }
}
