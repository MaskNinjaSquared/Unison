using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ConnectionStatusNotifyPolicyTests
    {
        [Theory]
        [InlineData("connected", true)]
        [InlineData("CONNECTED", true)]
        [InlineData("synced", true)]
        [InlineData("open", false)]
        [InlineData("connecting", false)]
        [InlineData(null, false)]
        public void Clears_toast_only_for_connected_or_synced(string status, bool expected)
        {
            Assert.Equal(
                expected,
                ConnectionStatusNotifyPolicy.ShouldClearReconnectRequiredToast(status));
        }
    }
}
