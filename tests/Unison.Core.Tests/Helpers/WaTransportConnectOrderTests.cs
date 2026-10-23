using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class WaTransportConnectOrderTests
    {
        [Fact]
        public void Desktop_always_prefers_stream()
        {
            Assert.True(WaTransportConnectOrder.PreferStreamSocketFirst(
                isMobile: false,
                preferStreamOnMobile: false));
            Assert.True(WaTransportConnectOrder.PreferStreamSocketFirst(
                isMobile: false,
                preferStreamOnMobile: true));
        }

        [Fact]
        public void Mobile_prefers_classic_unless_experiment_asks_stream()
        {
            Assert.False(WaTransportConnectOrder.PreferStreamSocketFirst(
                isMobile: true,
                preferStreamOnMobile: false));
            Assert.True(WaTransportConnectOrder.PreferStreamSocketFirst(
                isMobile: true,
                preferStreamOnMobile: true));
        }
    }
}
