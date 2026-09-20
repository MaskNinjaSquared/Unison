// =============================================================================
// Tests for SelfChatNaming.IsKnownFallback and GroupParticipantResolver.IsUsableDisplayLabel.
//
// The self-account label is injected by the UI in the current locale. Before this
// fix, only "Me" and "You" were recognized; the localized stand-in sailed through
// as a real contact name and could be cached in the group-participant label store.
// The list is now canonical (KnownFallbacks), and every caller consults it.
// =============================================================================
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class SelfChatNamingFallbackTests
    {
        private const string AnyJid = "5511999990000@s.whatsapp.net";

        // --- Every entry in KnownFallbacks is recognized -------------------------

        [Theory]
        [InlineData("You")]
        [InlineData("Me")]
        [InlineData("Você")]
        [InlineData("Anda")]
        [InlineData("Tu")]
        [InlineData("Tú")]
        [InlineData("Ty")]
        [InlineData("U")]
        public void Every_entry_in_known_fallbacks_is_recognized(string fallback)
        {
            // The array documents each locale this code path is expected to see.
            // If a translation is added to the array but the recognizer misses it,
            // the localized marker passes as a real contact name.
            Assert.True(SelfChatNaming.IsKnownFallback(fallback));
        }

        // --- Case insensitivity --------------------------------------------------

        [Theory]
        [InlineData("você")]
        [InlineData("VOCÊ")]
        [InlineData("you")]
        [InlineData("YOU")]
        [InlineData("me")]
        [InlineData("ME")]
        public void Recognition_ignores_case(string fallback)
        {
            // The localized resource might come from a different casing convention,
            // and user input is never normalized before reaching here.
            Assert.True(SelfChatNaming.IsKnownFallback(fallback));
        }

        // --- Surrounding whitespace ----------------------------------------------

        [Theory]
        [InlineData("  Você  ")]
        [InlineData(" You ")]
        [InlineData("\t Me \t")]
        public void Recognition_trims_surrounding_whitespace(string padded)
        {
            // Labels arrive with inconsistent trimming from various sources.
            Assert.True(SelfChatNaming.IsKnownFallback(padded));
        }

        // --- Real names that start with a fallback entry -------------------------

        [Theory]
        [InlineData("Youssef")]
        [InlineData("YoussefBenAli")]
        [InlineData("Tulio")]
        [InlineData("Túlio")]
        [InlineData("Tulipa")]
        [InlineData("Megan")]
        [InlineData("Melody")]
        [InlineData("Tyrone")]
        [InlineData("Tyler")]
        [InlineData("Anderson")]
        [InlineData("Andréia")]
        public void A_real_name_that_starts_with_a_fallback_is_not_recognized(string realName)
        {
            // The comparison must be equality, not prefix. Throwing out "Youssef"
            // because it begins with "You" would erase real contacts.
            Assert.False(SelfChatNaming.IsKnownFallback(realName));
        }

        // --- Null, empty, whitespace-only ----------------------------------------

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t")]
        public void Empty_or_whitespace_only_labels_are_not_fallbacks(string? label)
        {
            // Nothing to match against; returning true here would mark every
            // nameless row as "already named with a marker" and stop resolution.
            Assert.False(SelfChatNaming.IsKnownFallback(label!));
        }

        // --- Regression: IsUsableDisplayLabel now uses the shared list -----------

        [Theory]
        [InlineData("Você")]
        [InlineData("Me")]
        [InlineData("You")]
        [InlineData("Anda")]
        [InlineData("Tu")]
        [InlineData("Tú")]
        [InlineData("Ty")]
        [InlineData("U")]
        public void A_known_fallback_is_not_usable_as_a_display_label_in_any_locale(string fallback)
        {
            // Before the fix, only "Me" and "You" were rejected. A device running
            // in Portuguese would treat "Você" as a real name and cache it.
            Assert.False(GroupParticipantResolver.IsUsableDisplayLabel(fallback, AnyJid));
        }

        [Theory]
        [InlineData("você")]
        [InlineData("VOCÊ")]
        [InlineData(" Você ")]
        public void Casing_and_whitespace_do_not_bypass_the_fallback_guard(string variant)
        {
            // The same normalization that IsKnownFallback does must apply here,
            // or a differently-cased variant sneaks through.
            Assert.False(GroupParticipantResolver.IsUsableDisplayLabel(variant, AnyJid));
        }

        [Fact]
        public void A_legitimate_contact_name_remains_usable()
        {
            // The fix must not reject real names — the entire point of the
            // resolution is to replace placeholders with these.
            Assert.True(GroupParticipantResolver.IsUsableDisplayLabel("Maria", AnyJid));
        }

        [Theory]
        [InlineData("Ana Paula")]
        [InlineData("Youssef")]
        [InlineData("Tulio")]
        [InlineData("Megan")]
        public void Names_that_start_with_fallback_prefixes_are_still_usable(string name)
        {
            // Equality, not prefix match. "Youssef" contains "You" but is a real name.
            Assert.True(GroupParticipantResolver.IsUsableDisplayLabel(name, AnyJid));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Empty_labels_are_not_usable(string? label)
        {
            // A label with nothing in it is a placeholder, not a real name.
            Assert.False(GroupParticipantResolver.IsUsableDisplayLabel(label!, AnyJid));
        }

        [Fact]
        public void A_label_containing_an_at_sign_is_not_usable()
        {
            // Raw JIDs are placeholders, not names. This rule was already present;
            // this test documents it alongside the new fallback guard.
            Assert.False(GroupParticipantResolver.IsUsableDisplayLabel(
                "5511999990000@s.whatsapp.net", AnyJid));
        }

        [Fact]
        public void The_jids_own_bare_user_is_not_usable_as_its_name()
        {
            // The number echoed back as a label is a placeholder. This rule was
            // already present; documenting it here for completeness.
            Assert.False(GroupParticipantResolver.IsUsableDisplayLabel(
                "5511999990000", AnyJid));
        }
    }
}
