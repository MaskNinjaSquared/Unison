// =============================================================================
// Tests for ExplicitLogoutStreamCode.
// =============================================================================
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ExplicitLogoutStreamCodeTests
    {
        [Fact]
        public void Null_or_blank_is_not_logout()
        {
            Assert.False(ExplicitLogoutStreamCode.Matches(null));
            Assert.False(ExplicitLogoutStreamCode.Matches(""));
            Assert.False(ExplicitLogoutStreamCode.Matches("  "));
        }

        [Fact]
        public void Http_style_auth_codes_are_logout()
        {
            Assert.True(ExplicitLogoutStreamCode.Matches("401"));
            Assert.True(ExplicitLogoutStreamCode.Matches("403"));
            Assert.True(ExplicitLogoutStreamCode.Matches(" 401 "));
        }

        [Fact]
        public void Device_removed_tokens_are_logout()
        {
            Assert.True(ExplicitLogoutStreamCode.Matches("device_removed"));
            Assert.True(ExplicitLogoutStreamCode.Matches("device-removed"));
            Assert.True(ExplicitLogoutStreamCode.Matches("DEVICE_REMOVED"));
        }

        [Fact]
        public void Transient_codes_are_not_logout()
        {
            Assert.False(ExplicitLogoutStreamCode.Matches("440"));
            Assert.False(ExplicitLogoutStreamCode.Matches("500"));
            Assert.False(ExplicitLogoutStreamCode.Matches("408"));
            Assert.False(ExplicitLogoutStreamCode.Matches("timeout"));
        }
    }
}
