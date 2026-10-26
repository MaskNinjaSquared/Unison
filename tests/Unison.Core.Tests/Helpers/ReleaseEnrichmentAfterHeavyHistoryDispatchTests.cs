using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ReleaseEnrichmentAfterHeavyHistoryDispatchTests
    {
        [Fact]
        public void Defers_when_catch_up_active()
        {
            var host = new FakeHost { CatchUpActive = true };
            ReleaseEnrichmentAfterHeavyHistoryDispatch.Run(host, "sqlite");
            Assert.True(host.MarkedAwaiting);
            Assert.False(host.Cleared);
            Assert.Null(host.EnrichmentReason);
        }

        [Fact]
        public void Schedules_enrichment_when_catch_up_idle()
        {
            var host = new FakeHost { CatchUpActive = false };
            ReleaseEnrichmentAfterHeavyHistoryDispatch.Run(host, "sqlite");
            Assert.False(host.MarkedAwaiting);
            Assert.True(host.Cleared);
            Assert.Equal("heavy-history:sqlite", host.EnrichmentReason);
        }

        private sealed class FakeHost : IReleaseEnrichmentAfterHeavyHistoryHost
        {
            public bool CatchUpActive;
            public bool MarkedAwaiting;
            public bool Cleared;
            public string EnrichmentReason;

            public bool IsHistoryCatchUpActive => CatchUpActive;

            public void MarkEnrichmentAwaitingHeavyHistory() => MarkedAwaiting = true;

            public void ClearEnrichmentAwaitingHeavyHistory() => Cleared = true;

            public void TrySchedulePostMessageEnrichment(string reason) =>
                EnrichmentReason = reason;

            public void LogEnrichmentStillDeferred(string reason)
            {
            }

            public void WriteEnrichmentDeferredCatchUp(string reason)
            {
            }
        }
    }
}
