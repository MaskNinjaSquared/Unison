// =============================================================================
// Labels that are only a phone number must not count as a chat title, whoever
// the number belongs to. Sanitize itself keeps another person's number (see
// ContactLabelSanitizerTests); the title rule asks IsPhoneLikeLabel.
// =============================================================================
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ContactLabelSanitizerPhoneLikeTests
    {
        [Theory]
        [InlineData("5511988887777")]
        [InlineData("+55 11 98888-7777")]
        [InlineData("(11) 98888-7777")]
        public void A_label_that_is_only_a_phone_number_is_phone_like(string label)
        {
            Assert.True(ContactLabelSanitizer.IsPhoneLikeLabel(label));
        }

        [Theory]
        [InlineData("Ana Souza")]
        [InlineData("Ana 5511988887777")]
        [InlineData("007")]
        [InlineData("Leo🇮🇹")]
        public void A_name_or_a_short_nickname_is_not_phone_like(string label)
        {
            Assert.False(ContactLabelSanitizer.IsPhoneLikeLabel(label));
        }
    }
}
