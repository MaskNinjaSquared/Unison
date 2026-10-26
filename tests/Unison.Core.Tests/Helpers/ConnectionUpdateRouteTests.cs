using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ConnectionUpdateRouteTests
    {
        [Fact]
        public void Classifies_restart_and_close_by_registration()
        {
            Assert.Equal(
                ConnectionUpdateRouteKind.Restart,
                ConnectionUpdateRoute.Classify("restart", registered: false));
            Assert.Equal(
                ConnectionUpdateRouteKind.RegisteredClose,
                ConnectionUpdateRoute.Classify("close", registered: true));
            Assert.Equal(
                ConnectionUpdateRouteKind.UnregisteredClose,
                ConnectionUpdateRoute.Classify("close", registered: false));
            Assert.Equal(
                ConnectionUpdateRouteKind.Other,
                ConnectionUpdateRoute.Classify("open", registered: true));
        }
    }
}
