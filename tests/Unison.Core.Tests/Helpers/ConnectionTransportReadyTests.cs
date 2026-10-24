using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ConnectionTransportReadyTests
    {
        [Theory]
        [InlineData(true, true, true, true)]
        [InlineData(true, true, false, false)]
        [InlineData(true, false, true, false)]
        [InlineData(false, true, true, false)]
        public void IsReady_requires_socket_connected_and_handshake(
            bool hasSocket,
            bool connected,
            bool handshake,
            bool expected)
        {
            Assert.Equal(
                expected,
                ConnectionTransportReady.IsReady(hasSocket, connected, handshake));
        }

        [Fact]
        public void HasHealthyRegistered_requires_transport_and_registered_auth()
        {
            Assert.True(ConnectionTransportReady.HasHealthyRegistered(true, true, true));
            Assert.False(ConnectionTransportReady.HasHealthyRegistered(true, true, false));
            Assert.False(ConnectionTransportReady.HasHealthyRegistered(false, true, true));
        }

        [Fact]
        public void HasHealthyUnregistered_requires_transport_and_unregistered()
        {
            Assert.True(ConnectionTransportReady.HasHealthyUnregistered(true, true));
            Assert.False(ConnectionTransportReady.HasHealthyUnregistered(true, false));
            Assert.False(ConnectionTransportReady.HasHealthyUnregistered(false, true));
        }
    }
}
