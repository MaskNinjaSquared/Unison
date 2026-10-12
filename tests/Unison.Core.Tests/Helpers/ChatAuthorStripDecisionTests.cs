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
    }
}
