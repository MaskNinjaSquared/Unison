using System;
using Unison.Socket.Session;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class FatalStreamCodeFromExceptionTests
    {
        [Fact]
        public void Prefers_typed_WaConnectionException_reason()
        {
            var inner = new WaConnectionException(
                "gone",
                DisconnectReason.LoggedOut);
            var wrapped = new Exception("outer", inner);

            Assert.Equal("401", FatalStreamCodeFromException.TryRead(wrapped));
        }

        [Fact]
        public void Falls_back_to_message_text()
        {
            Assert.Equal(
                "401",
                FatalStreamCodeFromException.TryRead(new Exception("Logged out of device")));
        }
    }
}
