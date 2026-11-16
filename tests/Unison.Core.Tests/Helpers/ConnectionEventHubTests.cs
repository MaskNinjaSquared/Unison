using System;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ConnectionEventHubTests
    {
        [Fact]
        public void Raises_reach_subscribers_of_the_event_source_contract()
        {
            var hub = new ConnectionEventHub();
            IConnectionEventSource source = hub;
            string qr = null;
            string status = null;
            bool expired = false;
            bool initialized = false;
            SessionClearedEventArgs cleared = null;
            Exception error = null;
            string streamError = null;
            string invalid = null;
            source.OnQRCodeReceived += (s, e) => qr = e;
            source.OnConnectionUpdate += (s, e) => status = e;
            source.OnQrExpired += (s, e) => expired = true;
            source.OnSessionInitialized += (s, e) => initialized = true;
            source.OnSessionCleared += (s, e) => cleared = e;
            source.OnError += (s, e) => error = e;
            source.OnStreamError += (s, e) => streamError = e;
            source.OnInvalidSessionSuspected += (s, e) => invalid = e;

            var args = new SessionClearedEventArgs(true);
            var boom = new InvalidOperationException("x");
            hub.RaiseQrCodeReceived("qr");
            hub.RaiseConnectionUpdate("open");
            hub.RaiseQrExpired();
            hub.RaiseSessionInitialized();
            hub.RaiseSessionCleared(args);
            hub.RaiseError(boom);
            hub.RaiseStreamError("515");
            hub.RaiseInvalidSessionSuspected("close");

            Assert.Equal("qr", qr);
            Assert.Equal("open", status);
            Assert.True(expired);
            Assert.True(initialized);
            Assert.Same(args, cleared);
            Assert.Same(boom, error);
            Assert.Equal("515", streamError);
            Assert.Equal("close", invalid);
        }

        [Fact]
        public void Raising_without_subscribers_is_a_no_op()
        {
            var hub = new ConnectionEventHub();

            hub.RaiseConnectionUpdate("open");
            hub.RaiseError(new Exception());
        }

        [Fact]
        public void Current_status_is_whatever_was_last_published()
        {
            var hub = new ConnectionEventHub();

            Assert.Null(hub.CurrentConnectionStatus);
            hub.CurrentConnectionStatus = "connecting";

            Assert.Equal("connecting", ((IConnectionEventSource)hub).CurrentConnectionStatus);
        }
    }
}
