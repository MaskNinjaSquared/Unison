// =============================================================================
// Tests for ParticipantStripIdentity — LID vs PN for the group strip tip.
// =============================================================================
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ParticipantStripIdentityTests
    {
        [Fact]
        public void A_phone_participant_wins()
        {
            Assert.Equal(
                "5511988887777@s.whatsapp.net",
                ParticipantStripIdentity.PreferForDisplay(
                    "5511988887777@s.whatsapp.net",
                    "199999999999@lid"));
        }

        [Fact]
        public void A_phone_alt_wins_over_a_lid_participant()
        {
            Assert.Equal(
                "5511988887777@s.whatsapp.net",
                ParticipantStripIdentity.PreferForDisplay(
                    "199999999999@lid",
                    "5511988887777@s.whatsapp.net"));
        }

        [Fact]
        public void Lid_alone_is_kept_when_no_phone_is_known()
        {
            Assert.Equal(
                "199999999999@lid",
                ParticipantStripIdentity.PreferForDisplay("199999999999@lid"));
        }

        [Theory]
        [InlineData(null, null)]
        [InlineData("", "")]
        [InlineData("   ", null)]
        public void Nothing_known_yields_null(string? participant, string? alt)
        {
            Assert.Null(ParticipantStripIdentity.PreferForDisplay(participant, alt));
        }
    }
}
