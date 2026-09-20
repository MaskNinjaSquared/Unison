// =============================================================================
// Tests for GroupAvatarFallbackDecision.
// =============================================================================
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class GroupAvatarFallbackDecisionTests
    {
        [Fact]
        public void Url_present_never_falls_back()
        {
            Assert.False(GroupAvatarFallbackDecision.ShouldTry("https://cdn/x", isNotFound: true, failureReason: null));
            Assert.False(GroupAvatarFallbackDecision.ShouldTry("https://cdn/x", isNotFound: false, failureReason: "server-error:404"));
        }

        [Fact]
        public void Not_found_with_empty_url_falls_back()
        {
            Assert.True(GroupAvatarFallbackDecision.ShouldTry(null, isNotFound: true, failureReason: null));
            Assert.True(GroupAvatarFallbackDecision.ShouldTry("", isNotFound: true, failureReason: null));
            Assert.True(GroupAvatarFallbackDecision.ShouldTry("  ", isNotFound: true, failureReason: null));
        }

        [Theory]
        [InlineData("server-error:401")]
        [InlineData("server-error:404")]
        [InlineData("server-error:406")]
        [InlineData("SERVER-ERROR:404")]
        public void Selected_server_errors_fall_back(string reason)
        {
            Assert.True(GroupAvatarFallbackDecision.ShouldTry(null, isNotFound: false, failureReason: reason));
        }

        [Fact]
        public void Other_failures_do_not_fall_back()
        {
            Assert.False(GroupAvatarFallbackDecision.ShouldTry(null, isNotFound: false, failureReason: "timeout"));
            Assert.False(GroupAvatarFallbackDecision.ShouldTry(null, isNotFound: false, failureReason: "server-error:500"));
            Assert.False(GroupAvatarFallbackDecision.ShouldTry(null, isNotFound: false, failureReason: null));
        }
    }
}
