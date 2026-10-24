using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class StartConnectionHealthMonitorDispatchTests
    {
        [Fact]
        public void Null_socket_stops_only()
        {
            var host = new FakeHost();
            StartConnectionHealthMonitorDispatch.Run(host, null);
            Assert.Equal("restart", host.StopReason);
            Assert.False(host.LoopStarted);
            Assert.False(host.PeriodicStarted);
        }

        [Fact]
        public void Socket_starts_loop_and_periodic()
        {
            var host = new FakeHost();
            var socket = new object();
            StartConnectionHealthMonitorDispatch.Run(host, socket);
            Assert.Same(socket, host.LoopSocket);
            Assert.True(host.PeriodicStarted);
        }

        private sealed class FakeHost : IStartConnectionHealthMonitorHost
        {
            public string StopReason;
            public bool LoopStarted;
            public object LoopSocket;
            public bool PeriodicStarted;

            public void StopConnectionHealthMonitor(string reason) => StopReason = reason;

            public void StartConnectionHealthLoop(object socket)
            {
                LoopStarted = true;
                LoopSocket = socket;
            }

            public void StartPeriodicFullHistorySafety() => PeriodicStarted = true;
        }
    }
}
