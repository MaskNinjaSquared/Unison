// =============================================================================
// Tests for ChatAuthorStripDecision — what the list strip does with one resolve attempt.
// =============================================================================
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ChatAuthorStripDecisionTests
    {
        [Fact]
        public void A_name_replaces_a_placeholder()
        {
            Assert.Equal(
                ChatAuthorStripAction.ShowName,
                ChatAuthorStripDecision.Decide(
                    currentSenderName: null,
                    currentIsRealName: false,
                    resolvedName: "Ana",
                    resolvedIsRealName: true,
                    hasNumber: true));
        }

        [Fact]
        public void The_same_name_is_left_alone()
        {
            Assert.Equal(
                ChatAuthorStripAction.Keep,
                ChatAuthorStripDecision.Decide(
                    currentSenderName: "Ana",
                    currentIsRealName: true,
                    resolvedName: "Ana",
                    resolvedIsRealName: true,
                    hasNumber: true));
        }

        /// <summary>
        /// Two valid answers for the same person (a live push name and a roster name) must not take
        /// turns on the row, or the strip flickers between them every time a name lands.
        /// </summary>
        [Fact]
        public void One_real_name_is_not_traded_for_another()
        {
            Assert.Equal(
                ChatAuthorStripAction.Keep,
                ChatAuthorStripDecision.Decide(
                    currentSenderName: "Ana Souza",
                    currentIsRealName: true,
                    resolvedName: "Ana",
                    resolvedIsRealName: true,
                    hasNumber: true));
        }

        [Fact]
        public void Without_a_name_the_number_is_shown()
        {
            Assert.Equal(
                ChatAuthorStripAction.ShowNumber,
                ChatAuthorStripDecision.Decide(
                    currentSenderName: null,
                    currentIsRealName: false,
                    resolvedName: null,
                    resolvedIsRealName: false,
                    hasNumber: true));
        }

        [Fact]
        public void Without_a_name_and_without_a_number_nothing_is_written()
        {
            Assert.Equal(
                ChatAuthorStripAction.Keep,
                ChatAuthorStripDecision.Decide(
                    currentSenderName: null,
                    currentIsRealName: false,
                    resolvedName: null,
                    resolvedIsRealName: false,
                    hasNumber: false));
        }

        [Fact]
        public void A_number_never_replaces_a_name_already_on_the_row()
        {
            Assert.Equal(
                ChatAuthorStripAction.Keep,
                ChatAuthorStripDecision.Decide(
                    currentSenderName: "Ana",
                    currentIsRealName: true,
                    resolvedName: null,
                    resolvedIsRealName: false,
                    hasNumber: true));
        }

        /// <summary>
        /// The device shape: the row knows "Lindsay Hefner" but renders the sender's digits,
        /// because the prefix and the name live in separate columns that are carried forward
        /// independently. Decide only reads the name, so it reports Keep — and the row must still
        /// be recognised as needing its prefix rewritten.
        /// </summary>
        [Fact]
        public void A_digits_prefix_beside_a_known_name_is_a_disagreement()
        {
            Assert.Equal(
                ChatAuthorStripAction.Keep,
                ChatAuthorStripDecision.Decide(
                    currentSenderName: "Lindsay Hefner",
                    currentIsRealName: true,
                    resolvedName: null,
                    resolvedIsRealName: false,
                    hasNumber: true));

            Assert.True(
                ChatAuthorStripDecision.AuthorPrefixDisagrees(
                    "17186831148: ",
                    "Lindsay Hefner"));
        }

        [Fact]
        public void A_prefix_that_already_shows_the_name_agrees()
        {
            Assert.False(
                ChatAuthorStripDecision.AuthorPrefixDisagrees("Ana: ", "Ana"));
        }

        /// <summary>
        /// A blank prefix next to a known name is still wrong — the row knows who spoke and is
        /// showing nobody.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void A_blank_prefix_beside_a_known_name_disagrees(string prefix)
        {
            Assert.True(ChatAuthorStripDecision.AuthorPrefixDisagrees(prefix, "Ana"));
        }

        /// <summary>
        /// With no name known there is nothing to disagree with: the digits are the documented
        /// stand-in, not a defect.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Without_a_known_name_nothing_disagrees(string senderName)
        {
            Assert.False(
                ChatAuthorStripDecision.AuthorPrefixDisagrees("17186831148: ", senderName));
        }
    }
}
