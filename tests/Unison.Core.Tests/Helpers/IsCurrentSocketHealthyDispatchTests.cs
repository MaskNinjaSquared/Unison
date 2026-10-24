using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class IsCurrentSocketHealthyDispatchTests
    {
        [Fact]
        public async Task Null_socket_is_not_ready()
        {
            var host = new FakeHost { Socket = null };
            Assert.False(await IsCurrentSocketHealthyDispatch.RunAsync(host));
        }

        [Fact]
        public async Task Fresh_connected_handshake_is_healthy()
        {
            var host = new FakeHost
            {
                Socket = new object(),
                Connected = true,
                Handshake = true,
                Fresh = true,
                Stalled = false
            };
            Assert.True(await IsCurrentSocketHealthyDispatch.RunAsync(host));
            Assert.False(host.Disconnected);
        }

        [Fact]
        public async Task Probe_success_returns_true()
        {
            var host = new FakeHost
            {
                Socket = new object(),
                Connected = true,
                Handshake = true,
                Fresh = false,
                Stalled = false,
                ProbeOk = true
            };
            Assert.True(await IsCurrentSocketHealthyDispatch.RunAsync(host));
        }

        [Fact]
        public async Task Tear_down_disconnects_and_clears()
        {
            var socket = new object();
            var host = new FakeHost
            {
                Socket = socket,
                Connected = true,
                Handshake = true,
                Fresh = true,
                Stalled = true
            };
            Assert.False(await IsCurrentSocketHealthyDispatch.RunAsync(host));
            Assert.True(host.Disconnected);
            Assert.True(host.Cleared);
            Assert.True(host.LoggedStall);
        }

        private sealed class FakeHost : IIsCurrentSocketHealthyHost
        {
            public object Socket;
            public bool Connected;
            public bool Handshake;
            public bool Fresh;
            public bool Stalled;
            public bool ProbeOk;
            public bool Disconnected;
            public bool Cleared;
            public bool LoggedStall;
            public bool SuppressReconnect { get; set; }

            public object CurrentSocket => Socket;

            public bool SocketHasStalledNodeProcessing(object socket) => Stalled;

            public bool SocketHasFreshConnection(object socket) => Fresh;

            public bool SocketIsConnected(object socket) => Connected;

            public bool SocketIsHandshakeComplete(object socket) => Handshake;

            public int SocketQueuedNodeProcessingCount(object socket) => 3;

            public void LogNodeQueueStalled(int depth) => LoggedStall = true;

            public Task<bool> ProbeSocketConnectionAsync(object socket) =>
                Task.FromResult(ProbeOk);

            public void DisconnectSocket(object socket) => Disconnected = true;

            public void ClearCurrentSocketIfSame(object socket) => Cleared = true;
        }
    }
}
