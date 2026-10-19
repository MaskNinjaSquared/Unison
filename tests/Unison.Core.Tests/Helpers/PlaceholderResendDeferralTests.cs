using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class PlaceholderResendDeferralTests
    {
        [Fact]
        public void The_first_matching_reason_wins()
        {
            string reason;
            Assert.True(PlaceholderResendDeferral.IsDeferred(true, true, true, out reason));
            Assert.Equal("reconnect-replay-active", reason);
            Assert.True(PlaceholderResendDeferral.IsDeferred(false, true, true, out reason));
            Assert.Equal("awaiting-initial-sync", reason);
            Assert.True(PlaceholderResendDeferral.IsDeferred(false, false, true, out reason));
            Assert.Equal("history-on-demand-in-flight", reason);
            Assert.False(PlaceholderResendDeferral.IsDeferred(false, false, false, out reason));
            Assert.Null(reason);
        }
    }
}
