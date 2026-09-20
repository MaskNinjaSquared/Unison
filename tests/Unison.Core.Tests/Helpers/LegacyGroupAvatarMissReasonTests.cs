// =============================================================================
// Tests for LegacyGroupAvatarMissReason.
// =============================================================================
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class LegacyGroupAvatarMissReasonTests
    {
        [Theory]
        [InlineData("server-error:404")]
        [InlineData("server-error:406")]
        [InlineData("no-picture")]
        [InlineData("SERVER-ERROR:404")]
        public void Legacy_miss_reasons_match(string reason)
        {
            Assert.True(LegacyGroupAvatarMissReason.Matches(reason));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("timeout")]
        [InlineData("server-error:401")]
        [InlineData("server-error:500")]
        public void Other_reasons_do_not_match(string reason)
        {
            Assert.False(LegacyGroupAvatarMissReason.Matches(reason));
        }
    }
}
