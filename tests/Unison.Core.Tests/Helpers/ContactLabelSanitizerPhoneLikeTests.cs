// =============================================================================
// Address-book / push labels that are only a phone number must not count as names.
// =============================================================================
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ContactLabelSanitizerPhoneLikeTests
    {
        private readonly ContactLabelSanitizer _sanitizer = new ContactLabelSanitizer(new NoMarkers());

        [Theory]
        [InlineData("5511988887777")]
        [InlineData("+55 11 98888-7777")]
        [InlineData("(11) 98888-7777")]
        public void A_label_that_is_only_a_phone_number_is_rejected(string label)
        {
            ContactLabelResult result = _sanitizer.Sanitize(label, "5511988887777@s.whatsapp.net");

            Assert.False(result.IsUsable);
            Assert.Equal(ContactLabelRejection.PhoneEcho, result.Rejection);
        }

        [Fact]
        public void A_real_name_is_kept()
        {
            ContactLabelResult result = _sanitizer.Sanitize("Ana Souza", "5511988887777@s.whatsapp.net");

            Assert.True(result.IsUsable);
            Assert.Equal("Ana Souza", result.Label);
        }

        [Fact]
        public void A_national_number_is_rejected_for_its_international_address()
        {
            // Same number, country code dropped: the digits are the tail of the address's.
            ContactLabelResult result = _sanitizer.Sanitize("98888-7777", "5511988887777@s.whatsapp.net");

            Assert.False(result.IsUsable);
            Assert.Equal(ContactLabelRejection.PhoneEcho, result.Rejection);
        }

        [Fact]
        public void A_different_number_is_still_a_label()
        {
            // It carries information the address does not, so refusing it would lose data.
            Assert.True(_sanitizer.Sanitize("5511911112222", "5511988887777@s.whatsapp.net").IsUsable);
        }

        [Fact]
        public void A_number_offered_for_a_lid_address_is_kept()
        {
            // A LID's digits are an opaque identifier, never the contact's number, so the two
            // can never match. The label is the only place that number exists — keep it, and
            // let GroupParticipantResolver decide it is not a display name.
            Assert.True(_sanitizer.Sanitize("5511988887777", "199999999999@lid").IsUsable);
        }

        [Fact]
        public void A_short_numeric_nickname_survives_the_shape_check()
        {
            // Below the digit floor, a numeric label is a nickname rather than a number.
            Assert.True(_sanitizer.Sanitize("007", "5511988887777@s.whatsapp.net").IsUsable);
        }

        private sealed class NoMarkers : ISelfMarkerNaming
        {
            public bool IsMarkerLabel(string label) => false;

            public string StripMarker(string label) => label;
        }
    }
}
