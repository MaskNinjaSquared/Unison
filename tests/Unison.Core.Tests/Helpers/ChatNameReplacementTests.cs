// =============================================================================
// Tests for ChatNameReplacement.
//
// The rule that decides whether a chat row's title changes under the user. It
// existed as two copies inside WhatsAppService that had drifted apart; these
// pin the single version.
// =============================================================================
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ChatNameReplacementTests
    {
        // --- nothing to do ---------------------------------------------------

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void An_empty_resolved_name_never_replaces_anything(string? resolved)
        {
            // The drift between the two former copies was here: one used
            // IsNullOrEmpty, so a whitespace-only name could be written over a
            // placeholder label. Unified on the stricter check.
            Assert.False(ChatNameReplacement.ShouldReplace(
                currentName: "+55 11 98888-8888",
                resolvedName: resolved,
                resolvedMeaningful: false,
                existingMeaningful: false,
                isGroup: false));
        }

        [Fact]
        public void An_identical_name_is_not_rewritten()
        {
            // Writing it anyway would raise PropertyChanged and repaint the row.
            Assert.False(ChatNameReplacement.ShouldReplace(
                currentName: "Ana",
                resolvedName: "Ana",
                resolvedMeaningful: true,
                existingMeaningful: true,
                isGroup: false));
        }

        [Fact]
        public void Case_and_spacing_differences_do_count_as_a_change()
        {
            // Ordinal comparison, so "ana" replacing "Ana" is a real update.
            Assert.True(ChatNameReplacement.ShouldReplace(
                currentName: "Ana",
                resolvedName: "ana",
                resolvedMeaningful: true,
                existingMeaningful: true,
                isGroup: false));
        }

        // --- contacts --------------------------------------------------------

        [Fact]
        public void A_real_name_replaces_a_phone_number()
        {
            Assert.True(ChatNameReplacement.ShouldReplace(
                currentName: "+55 11 98888-8888",
                resolvedName: "Ana",
                resolvedMeaningful: true,
                existingMeaningful: false,
                isGroup: false));
        }

        [Fact]
        public void A_real_name_replaces_another_real_name()
        {
            // The address book changed, or a push name arrived; newest wins.
            Assert.True(ChatNameReplacement.ShouldReplace(
                currentName: "Ana",
                resolvedName: "Ana Souza",
                resolvedMeaningful: true,
                existingMeaningful: true,
                isGroup: false));
        }

        [Fact]
        public void A_phone_number_does_not_replace_a_real_name()
        {
            Assert.False(ChatNameReplacement.ShouldReplace(
                currentName: "Ana",
                resolvedName: "+55 11 98888-8888",
                resolvedMeaningful: false,
                existingMeaningful: true,
                isGroup: false));
        }

        [Fact]
        public void A_phone_number_fills_a_blank_label()
        {
            Assert.True(ChatNameReplacement.ShouldReplace(
                currentName: "",
                resolvedName: "+55 11 98888-8888",
                resolvedMeaningful: false,
                existingMeaningful: false,
                isGroup: false));
        }

        [Fact]
        public void A_contact_may_be_named_after_a_blacklisted_token()
        {
            // The invite-link blacklist is about subjects WhatsApp synthesises for a
            // group. A person is entitled to such a name.
            Assert.True(ChatNameReplacement.ShouldReplace(
                currentName: "+55 11 98888-8888",
                resolvedName: "Invite",
                resolvedMeaningful: true,
                existingMeaningful: false,
                isGroup: false));

            Assert.True(ChatNameReplacement.ShouldReplace(
                currentName: "Ana",
                resolvedName: "Invite",
                resolvedMeaningful: true,
                existingMeaningful: true,
                isGroup: false));
        }

        // --- groups ----------------------------------------------------------

        [Fact]
        public void A_group_subject_replaces_a_placeholder()
        {
            Assert.True(ChatNameReplacement.ShouldReplace(
                currentName: "120363000000000000",
                resolvedName: "Família",
                resolvedMeaningful: true,
                existingMeaningful: false,
                isGroup: true));
        }

        [Fact]
        public void An_invite_link_subject_does_not_replace_a_readable_group_name()
        {
            Assert.False(ChatNameReplacement.ShouldReplace(
                currentName: "Família",
                resolvedName: "Group invite",
                resolvedMeaningful: true,
                existingMeaningful: true,
                isGroup: true));
        }

        [Fact]
        public void An_invite_link_subject_is_better_than_nothing()
        {
            Assert.True(ChatNameReplacement.ShouldReplace(
                currentName: "120363000000000000",
                resolvedName: "Group invite",
                resolvedMeaningful: true,
                existingMeaningful: false,
                isGroup: true));
        }

        [Fact]
        public void The_blacklist_is_the_only_difference_between_a_group_and_a_contact()
        {
            // Same inputs, same answer, whenever the name is not blacklisted.
            foreach (var resolvedMeaningful in new[] { true, false })
            {
                foreach (var existingMeaningful in new[] { true, false })
                {
                    Assert.Equal(
                        ChatNameReplacement.ShouldReplace(
                            "current", "Família", resolvedMeaningful, existingMeaningful, isGroup: false),
                        ChatNameReplacement.ShouldReplace(
                            "current", "Família", resolvedMeaningful, existingMeaningful, isGroup: true));
                }
            }
        }
    }
}
