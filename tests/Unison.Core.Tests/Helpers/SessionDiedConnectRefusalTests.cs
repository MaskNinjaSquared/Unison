using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class SessionDiedConnectRefusalTests
    {
        [Fact]
        public void Allows_when_fatal_latch_clear()
        {
            Assert.False(SessionDiedConnectRefusal.ShouldRefuse(false, true, true));
        }

        [Fact]
        public void Refuses_when_fatal_and_registered()
        {
            Assert.True(SessionDiedConnectRefusal.ShouldRefuse(true, false, true));
        }

        [Fact]
        public void Refuses_when_fatal_and_auth_wiped()
        {
            Assert.True(SessionDiedConnectRefusal.ShouldRefuse(true, true, false));
        }

        [Fact]
        public void Allows_qr_when_fatal_but_unregistered_auth_present()
        {
            // Fatal latch + auth present but not registered → QR may proceed.
            Assert.False(SessionDiedConnectRefusal.ShouldRefuse(true, false, false));
        }
    }
}
