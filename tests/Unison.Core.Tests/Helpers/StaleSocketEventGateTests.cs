using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class StaleSocketEventGateTests
    {
        [Fact]
        public void Ignores_stale_unless_explicit_logout()
        {
            Assert.True(StaleSocketEventGate.ShouldIgnore(
                isCurrentSocket: false,
                isExplicitLogout: false));
            Assert.False(StaleSocketEventGate.ShouldIgnore(
                isCurrentSocket: false,
                isExplicitLogout: true));
            Assert.False(StaleSocketEventGate.ShouldIgnore(
                isCurrentSocket: true,
                isExplicitLogout: false));
        }
    }
}
