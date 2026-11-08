// =============================================================================
// Tests for StanzaNotifyPushNameUiPolicy.
// =============================================================================
using Unison.Core.Helpers.Contacts;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class StanzaNotifyPushNameUiPolicyTests
    {
        [Theory]
        [InlineData(true, true)]
        [InlineData(false, false)]
        public void Placeholder_ui_only_when_push_name_changed(
            bool changed,
            bool expected)
        {
            Assert.Equal(
                expected,
                StanzaNotifyPushNameUiPolicy.ShouldSchedulePlaceholderNameUi(changed));
        }
    }
}
