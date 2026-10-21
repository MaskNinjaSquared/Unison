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

        private sealed class NoMarkers : ISelfMarkerNaming
        {
            public bool IsMarkerLabel(string label) => false;

            public string StripMarker(string label) => label;
        }
    }
}
