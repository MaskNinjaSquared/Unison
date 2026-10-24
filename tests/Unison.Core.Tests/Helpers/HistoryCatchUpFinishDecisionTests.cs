using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class HistoryCatchUpFinishDecisionTests
    {
        [Fact]
        public void None_when_enrichment_not_released()
        {
            Assert.Equal(
                HistoryCatchUpFinishNext.None,
                HistoryCatchUpFinishDecision.Decide(false, true));
        }

        [Fact]
        public void Names_when_first_sync_hold_up()
        {
            Assert.Equal(
                HistoryCatchUpFinishNext.ReleaseListAfterNames,
                HistoryCatchUpFinishDecision.Decide(true, true));
        }

        [Fact]
        public void Enrichment_when_hold_idle()
        {
            Assert.Equal(
                HistoryCatchUpFinishNext.ScheduleEnrichment,
                HistoryCatchUpFinishDecision.Decide(true, false));
        }
    }
}
