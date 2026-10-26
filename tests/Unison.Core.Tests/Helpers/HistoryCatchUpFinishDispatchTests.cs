using System;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class HistoryCatchUpFinishDispatchTests
    {
        [Fact]
        public void Release_enrichment_false_stops_after_banner()
        {
            var host = new FakeHost { FirstSyncHold = true };
            HistoryCatchUpFinishDispatch.Run(host, "x", releaseEnrichment: false);

            Assert.True(host.Finished);
            Assert.True(host.BannerEnded);
            Assert.False(host.ListReleaseRequested);
            Assert.False(host.EnrichmentScheduled);
        }

        [Fact]
        public void First_sync_hold_requests_list_release_after_names()
        {
            var host = new FakeHost
            {
                FirstSyncHold = true,
                Accumulated = 3,
                InitialProcessed = 2
            };
            HistoryCatchUpFinishDispatch.Run(host, "done", releaseEnrichment: true);

            Assert.True(host.ListReleaseRequested);
            Assert.Equal(3, host.ListReleaseProcessed);
            Assert.Equal(3, host.ListReleaseTotal);
            Assert.False(host.EnrichmentScheduled);
        }

        [Fact]
        public void No_hold_schedules_enrichment()
        {
            var host = new FakeHost { FirstSyncHold = false };
            HistoryCatchUpFinishDispatch.Run(host, "done", releaseEnrichment: true);

            Assert.False(host.ListReleaseRequested);
            Assert.True(host.EnrichmentScheduled);
            Assert.True(host.EnrichmentCleared);
            Assert.True(host.AvatarsScheduled);
        }

        private sealed class FakeHost : IHistoryCatchUpFinishHost
        {
            public bool FirstSyncHold;
            public int Accumulated;
            public int InitialProcessed;
            public bool Finished;
            public bool BannerEnded;
            public bool ListReleaseRequested;
            public int ListReleaseProcessed;
            public int ListReleaseTotal;
            public bool EnrichmentScheduled;
            public bool EnrichmentCleared;
            public bool AvatarsScheduled;

            public bool IsFirstSyncListHoldInProgress => FirstSyncHold;
            public int SqliteHistoryConversationsAccumulated => Accumulated;
            public int InitialSyncProcessedConversations => InitialProcessed;

            public HistoryCatchUpProgress FinishCatchUpProgress()
            {
                Finished = true;
                return new HistoryCatchUpProgress { Round = 1 };
            }

            public void WriteCatchUpFinishJournal(string reason, HistoryCatchUpProgress last)
            {
            }

            public void EndHistoryCatchUpBanner(string reason) => BannerEnded = true;

            public void RequestFirstSyncListReleaseAfterNames(string reason, int processed, int total)
            {
                ListReleaseRequested = true;
                ListReleaseProcessed = processed;
                ListReleaseTotal = total;
            }

            public void ScheduleDeferredAvatars(string reason, TimeSpan delay) =>
                AvatarsScheduled = true;

            public void ClearEnrichmentAwaitingHeavyHistory() => EnrichmentCleared = true;

            public void TrySchedulePostMessageEnrichment(string reason) =>
                EnrichmentScheduled = true;
        }
    }
}
