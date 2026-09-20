// =============================================================================
// Tests for ContactLabelSanitizer.
//
// Which offered names are real names. One of these rules is spoof prevention,
// and it had no test before.
// =============================================================================
using System;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ContactLabelSanitizerTests
    {
        private const string Ana = "5511988887777@s.whatsapp.net";

        /// <summary>
        /// Stands in for the localized "(You)" marker without loading UWP resources.
        /// </summary>
        private sealed class FakeSelfMarker : ISelfMarkerNaming
        {
            public bool IsMarkerLabel(string label) =>
                string.Equals(label?.Trim(), "(You)", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(label?.Trim(), "You", StringComparison.OrdinalIgnoreCase);

            public string StripMarker(string label)
            {
                // null! throughout: Unison.Core is not annotated, and null is a meaningful
                // answer here — it means nothing was left once the marker came off.
                string? trimmed = label?.Trim();
                if (string.IsNullOrEmpty(trimmed))
                {
                    return null!;
                }

                if (trimmed!.EndsWith("(You)", StringComparison.OrdinalIgnoreCase))
                {
                    string head = trimmed.Substring(0, trimmed.Length - "(You)".Length).Trim();
                    return string.IsNullOrEmpty(head) ? null! : head;
                }

                return trimmed;
            }
        }

        private static ContactLabelSanitizer Sanitizer() => new ContactLabelSanitizer(new FakeSelfMarker());

        // --- Ordinary names ---------------------------------------------------

        [Fact]
        public void A_real_name_comes_through()
        {
            ContactLabelResult result = Sanitizer().Sanitize("Ana Paula", Ana);

            Assert.True(result.IsUsable);
            Assert.Equal("Ana Paula", result.Label);
        }

        [Fact]
        public void Surrounding_whitespace_is_dropped()
        {
            Assert.Equal("Ana", Sanitizer().Sanitize("  Ana  ", Ana).Label);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Nothing_offered_is_nothing_accepted(string? label)
        {
            ContactLabelResult result = Sanitizer().Sanitize(label, Ana);

            Assert.False(result.IsUsable);
            Assert.Equal(ContactLabelRejection.Empty, result.Rejection);
        }

        // --- Masked numbers ---------------------------------------------------

        [Theory]
        [InlineData("+55 ••••-1234")]
        [InlineData("+55 ****-1234")]
        [InlineData("~ +55 ●●●●-1234")]
        [InlineData("+1 …7777")]
        public void A_number_WhatsApp_already_hid_is_not_a_name(string? label)
        {
            Assert.Equal(ContactLabelRejection.MaskedPhone, Sanitizer().Sanitize(label, Ana).Rejection);
        }

        [Fact]
        public void A_name_that_merely_contains_a_star_is_still_a_name()
        {
            // The mask rule keys on a *short* digit count, not on the glyph alone.
            Assert.True(Sanitizer().Sanitize("*Ana*", Ana).IsUsable);
        }

        [Fact]
        public void A_full_number_with_punctuation_is_not_treated_as_masked()
        {
            // Too many visible digits to be a mask, so it survives this rule.
            Assert.NotEqual(
                ContactLabelRejection.MaskedPhone,
                Sanitizer().Sanitize("+55 11 98888-7777", "5511999990000@s.whatsapp.net").Rejection);
        }

        // --- Spoof prevention --------------------------------------------------

        [Fact]
        public void A_contact_calling_themselves_You_is_refused()
        {
            // Without this, their messages would render as the user's own.
            ContactLabelResult result = Sanitizer().Sanitize("(You)", Ana);

            Assert.False(result.IsUsable);
            Assert.Equal(ContactLabelRejection.SelfMarker, result.Rejection);
        }

        [Fact]
        public void The_refusal_applies_to_the_users_own_address_too()
        {
            // There it is not an attack, just useless: the numeric identity is kept.
            Assert.Equal(
                ContactLabelRejection.SelfMarker,
                Sanitizer().Sanitize("You", "5511999990000@s.whatsapp.net").Rejection);
        }

        [Fact]
        public void A_marker_glued_onto_a_real_name_is_removed_not_refused()
        {
            ContactLabelResult result = Sanitizer().Sanitize("Ana (You)", Ana);

            Assert.True(result.IsUsable);
            Assert.Equal("Ana", result.Label);
            Assert.True(result.MarkerStripped);
        }

        [Fact]
        public void A_name_with_no_marker_is_not_reported_as_stripped()
        {
            Assert.False(Sanitizer().Sanitize("Ana", Ana).MarkerStripped);
        }

        // --- Phone echo --------------------------------------------------------

        [Fact]
        public void A_contact_offering_their_own_number_as_their_name_adds_nothing()
        {
            ContactLabelResult result = Sanitizer().Sanitize("5511988887777", Ana);

            Assert.False(result.IsUsable);
            Assert.Equal(ContactLabelRejection.PhoneEcho, result.Rejection);
        }

        [Fact]
        public void The_same_number_written_differently_is_still_an_echo()
        {
            Assert.Equal(
                ContactLabelRejection.PhoneEcho,
                Sanitizer().Sanitize("+55 11 98888-7777", Ana).Rejection);
        }

        [Fact]
        public void Somebody_elses_number_is_left_alone()
        {
            // It carries information the address does not, so refusing it would lose data.
            Assert.True(Sanitizer().Sanitize("5511911112222", Ana).IsUsable);
        }

        [Fact]
        public void A_name_containing_letters_is_never_an_echo()
        {
            Assert.True(Sanitizer().Sanitize("Ana 5511988887777", Ana).IsUsable);
        }

        [Fact]
        public void A_short_numeric_nickname_is_not_an_echo()
        {
            // Below the digit floor, matching digits are coincidence.
            Assert.True(Sanitizer().Sanitize("123", "123@s.whatsapp.net").IsUsable);
        }

        [Fact]
        public void Without_an_address_to_compare_against_nothing_echoes()
        {
            Assert.True(Sanitizer().Sanitize("5511988887777", null).IsUsable);
        }

        // --- Wiring -------------------------------------------------------------

        [Fact]
        public void The_original_is_kept_on_every_refusal_so_it_can_be_logged()
        {
            Assert.Equal("(You)", Sanitizer().Sanitize("  (You)  ", Ana).Original);
            Assert.Equal("+55 ••••-1234", Sanitizer().Sanitize("+55 ••••-1234", Ana).Original);
        }

        [Fact]
        public void A_sanitizer_without_marker_rules_is_refused_at_construction()
        {
            Assert.Throws<ArgumentNullException>(() => new ContactLabelSanitizer(null));
        }
    }
}
