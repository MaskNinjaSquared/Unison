using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class WaTransportConnectAttemptTests
    {
        [Fact]
        public void Desktop_gets_stream_primary_and_classic_fallback()
        {
            var factory = new FakeFactory();
            WaTransportConnectAttempt attempt = WaTransportConnectAttempt.Create(
                factory,
                isMobile: false,
                preferStreamOnMobile: false);

            Assert.Equal("stream", attempt.Primary.Name);
            Assert.Equal("classic", attempt.Fallback.Name);
        }

        [Fact]
        public void Mobile_default_is_classic_only()
        {
            var factory = new FakeFactory();
            WaTransportConnectAttempt attempt = WaTransportConnectAttempt.Create(
                factory,
                isMobile: true,
                preferStreamOnMobile: false);

            Assert.Equal("classic", attempt.Primary.Name);
            Assert.Null(attempt.Fallback);
        }

        private sealed class FakeFactory : IWhatsAppTransportFactory
        {
            public IWhatsAppTransport CreateStreamSocketTransport() => new FakeTransport("stream");

            public IWhatsAppTransport CreateMessageWebSocketTransport() => new FakeTransport("classic");
        }

        private sealed class FakeTransport : IWhatsAppTransport
        {
            public FakeTransport(string name) => Name = name;

            public string Name { get; }
            public bool IsConnected => false;
            public bool IsOwnedByBroker => false;
            public string SocketId => null;

            public event System.Func<object, TransportMessageEventArgs, Task> MessageReceived
            {
                add { }
                remove { }
            }

            public event System.EventHandler<TransportClosedEventArgs> Closed
            {
                add { }
                remove { }
            }

            public Task ConnectAsync(System.Uri uri, IDictionary<string, string> headers) =>
                Task.CompletedTask;

            public Task SendAsync(byte[] data) => Task.CompletedTask;

            public Task CloseAsync(ushort code, string reason) => Task.CompletedTask;

            public Task<bool> TransferToBrokerAsync(string reason, System.Func<string, Task> beforeTransfer) =>
                Task.FromResult(false);

            public Task<bool> ReclaimFromBrokerAsync() => Task.FromResult(false);

            public void Dispose()
            {
            }
        }
    }
}
