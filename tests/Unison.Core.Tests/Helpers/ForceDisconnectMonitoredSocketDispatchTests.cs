using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ForceDisconnectMonitoredSocketDispatchTests
    {
        [Fact]
        public void Skips_when_socket_not_current()
        {
            var host = new FakeHost { IsCurrent = false };
            ForceDisconnectMonitoredSocketDispatch.Run(host);
            Assert.False(host.Disconnected);
            Assert.False(host.Cleared);
        }

        [Fact]
        public void Disconnects_and_clears_when_current()
        {
            var host = new FakeHost { IsCurrent = true };
            ForceDisconnectMonitoredSocketDispatch.Run(host);
            Assert.True(host.Disconnected);
            Assert.True(host.Cleared);
        }

        private sealed class FakeHost : IForceDisconnectMonitoredSocketHost
        {
            public bool IsCurrent;
            public bool Disconnected;
            public bool Cleared;

            public bool IsMonitoredSocketCurrent => IsCurrent;

            public void DisconnectMonitoredSocketBestEffort() => Disconnected = true;

            public void ClearCurrentSocketReferenceIfStillMonitored() => Cleared = true;
        }
    }
}
