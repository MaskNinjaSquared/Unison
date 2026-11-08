using Unison.Core.Helpers.Chats;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class SessionListingApplyPolicyTests
    {
        [Theory]
        [InlineData(false, false, true)]
        [InlineData(true, false, false)]
        [InlineData(true, true, true)]
        [InlineData(false, true, true)]
        public void ShouldApplyListing_skips_repeat_unless_forced(
            bool alreadyApplied,
            bool force,
            bool expectApply)
        {
            Assert.Equal(
                expectApply,
                SessionListingApplyPolicy.ShouldApplyListing(alreadyApplied, force));
        }
    }
}
