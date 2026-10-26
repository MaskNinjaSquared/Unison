using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class TearDownSocketForSoftReconnectDispatchTests
    {
        [Fact]
        public void Null_socket_is_noop()
        {
            var host = new FakeHost { Socket = null };
            TearDownSocketForSoftReconnectDispatch.Run(host);
            Assert.False(host.Disposed);
        }

        [Fact]
        public void Detaches_and_disposes_socket()
        {
            var socket = new object();
            var host = new FakeHost { Socket = socket };
            TearDownSocketForSoftReconnectDispatch.Run(host);
            Assert.Null(host.Socket);
            Assert.Same(socket, host.DisposedSocket);
            Assert.True(host.Disposed);
        }

        private sealed class FakeHost : ITearDownSocketForSoftReconnectHost
        {
            public object Socket;
            public object DisposedSocket;
            public bool Disposed;

            public object DetachCurrentSocket()
            {
                object stale = Socket;
                Socket = null;
                return stale;
            }

            public void DisconnectAndDisposeSocket(object socket)
            {
                DisposedSocket = socket;
                Disposed = true;
            }
        }
    }
}
