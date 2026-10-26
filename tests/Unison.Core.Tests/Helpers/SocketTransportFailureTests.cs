using System;
using System.IO;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class SocketTransportFailureTests
    {
        [Fact]
        public void Matches_timeout_io_and_known_message_tokens()
        {
            Assert.True(SocketTransportFailure.Matches(
                new TimeoutException("x"),
                socketConnected: true,
                handshakeComplete: true));
            Assert.True(SocketTransportFailure.Matches(
                new IOException("x"),
                socketConnected: true,
                handshakeComplete: true));
            Assert.True(SocketTransportFailure.Matches(
                new Exception("err 0x80072F7D"),
                socketConnected: true,
                handshakeComplete: true));
            Assert.True(SocketTransportFailure.Matches(
                new Exception("Secure Channel Failure"),
                socketConnected: true,
                handshakeComplete: true));
            Assert.True(SocketTransportFailure.Matches(
                new Exception("keep-alive failed"),
                socketConnected: true,
                handshakeComplete: true));
        }

        [Fact]
        public void Matches_when_socket_or_handshake_down()
        {
            Assert.True(SocketTransportFailure.Matches(
                new Exception("whatever"),
                socketConnected: false,
                handshakeComplete: true));
            Assert.True(SocketTransportFailure.Matches(
                new Exception("whatever"),
                socketConnected: true,
                handshakeComplete: false));
        }

        [Fact]
        public void Rejects_unrelated_error_on_healthy_socket()
        {
            Assert.False(SocketTransportFailure.Matches(
                new Exception("application refused"),
                socketConnected: true,
                handshakeComplete: true));
        }
    }
}
