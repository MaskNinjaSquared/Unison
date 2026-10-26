using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class DropCurrentSocketForFastResumeDispatchTests
    {
        [Fact]
        public void Null_socket_is_noop()
        {
            var host = new FakeHost { Socket = null, SuppressReconnect = false };
            DropCurrentSocketForFastResumeDispatch.Run(host);
            Assert.False(host.Disposed);
            Assert.False(host.SuppressReconnect);
        }

        [Fact]
        public void Suppresses_during_dispose_then_restores()
        {
            var host = new FakeHost { Socket = new object(), SuppressReconnect = false };
            DropCurrentSocketForFastResumeDispatch.Run(host);
            Assert.True(host.Disposed);
            Assert.False(host.SuppressReconnect);
            Assert.True(host.SawSuppressTrue);
        }

        private sealed class FakeHost : IDropCurrentSocketForFastResumeHost
        {
            public object Socket;
            public bool Disposed;
            public bool SawSuppressTrue;
            public bool SuppressReconnect { get; set; }

            public object TakeCurrentSocketOrNull()
            {
                object s = Socket;
                Socket = null;
                return s;
            }

            public void DisconnectAndDisposeSocket(object socket)
            {
                SawSuppressTrue = SuppressReconnect;
                Disposed = true;
            }
        }
    }
}
