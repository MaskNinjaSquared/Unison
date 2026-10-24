using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ReconnectHistoryCatchUpFloorGatesTests
    {
        [Fact]
        public void Proceeds_when_quiet_connected_and_visible()
        {
            Assert.Equal(
                ReconnectHistoryCatchUpFloorAction.Proceed,
                ReconnectHistoryCatchUpFloorGates.Decide(true, true, false, true));
        }

        [Fact]
        public void Mid_catch_up_ignores_visibility()
        {
            Assert.Equal(
                ReconnectHistoryCatchUpFloorAction.Proceed,
                ReconnectHistoryCatchUpFloorGates.Decide(true, true, true, false));
        }

        [Fact]
        public void Cold_abort_releases_enrichment()
        {
            Assert.Equal(
                ReconnectHistoryCatchUpFloorAction.AbortAndReleaseEnrichment,
                ReconnectHistoryCatchUpFloorGates.Decide(false, true, false, true));
            Assert.Equal(
                ReconnectHistoryCatchUpFloorAction.AbortAndReleaseEnrichment,
                ReconnectHistoryCatchUpFloorGates.Decide(true, true, false, false));
        }

        [Fact]
        public void Mid_catch_up_abort_keeps_enrichment()
        {
            Assert.Equal(
                ReconnectHistoryCatchUpFloorAction.AbortKeepEnrichment,
                ReconnectHistoryCatchUpFloorGates.Decide(false, true, true, true));
        }
    }
}
