using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class PairingRestartStreamCodeTests
    {
        [Fact]
        public void Only_515_matches()
        {
            Assert.True(PairingRestartStreamCode.Matches("515"));
            Assert.False(PairingRestartStreamCode.Matches("401"));
            Assert.False(PairingRestartStreamCode.Matches(null));
        }
    }
}
