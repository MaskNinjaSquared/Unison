using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class HydrationDayRetryGatesTests
    {
        [Fact]
        public void Arms_only_while_stale_under_cap()
        {
            Assert.True(HydrationDayRetryGates.ShouldArm(true, 1, 5));
            Assert.False(HydrationDayRetryGates.ShouldArm(false, 1, 5));
            Assert.False(HydrationDayRetryGates.ShouldArm(true, 5, 5));
        }
    }
}
