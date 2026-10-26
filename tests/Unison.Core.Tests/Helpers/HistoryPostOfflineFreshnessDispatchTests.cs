using System;
using System.Threading.Tasks;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class HistoryPostOfflineFreshnessDispatchTests
    {
        [Fact]
        public async Task Finishes_when_fresh_and_catch_up_idle()
        {
            var host = new FakeHost { Stale = false, CatchUpActive = false };
            await HistoryPostOfflineFreshnessDispatch.RunAsync(host, offlineCount: 0);
            Assert.Equal("offline-fresh", host.FinishReason);
            Assert.Null(host.RequestedReason);
        }

        [Fact]
        public async Task Continues_catch_up_when_fresh_drain_has_messages()
        {
            var host = new FakeHost
            {
                Stale = false,
                CatchUpActive = true,
                DrainAction = HistoryCatchUpDrainAction.Continue
            };
            await HistoryPostOfflineFreshnessDispatch.RunAsync(host, offlineCount: 3);
            Assert.Equal("catch-up-continue-offline:3", host.RequestedReason);
            Assert.True(host.NotedProgress);
        }

        [Fact]
        public async Task Requests_full_history_when_still_stale()
        {
            var host = new FakeHost { Stale = true, StaleReason = "ageMinutes=40" };
            await HistoryPostOfflineFreshnessDispatch.RunAsync(host, offlineCount: 1);
            Assert.StartsWith("offline-stale:1:", host.RequestedReason);
            Assert.True(host.MarkedAwaiting);
        }

        private sealed class FakeHost : IHistoryPostOfflineFreshnessHost
        {
            public bool Stale { get; set; }
            public string StaleReason { get; set; }
            public bool CatchUpActive { get; set; }
            public HistoryCatchUpDrainAction DrainAction { get; set; } =
                HistoryCatchUpDrainAction.Finish;
            public string FinishReason { get; private set; }
            public string RequestedReason { get; private set; }
            public bool NotedProgress { get; private set; }
            public bool MarkedAwaiting { get; private set; }

            public DateTime LastHistorySyncReceivedUtc => DateTime.MinValue;
            public string LastHistorySyncTypeOrUnknown => "<unknown>";
            public bool IsHistoryCatchUpActive => CatchUpActive;
            public int CatchUpStagnantCyclesBeforeStop => 3;

            public void NoteCatchUpProgress(string reason) => NotedProgress = true;

            public bool TryGetFreshnessStaleReason(DateTime nowUtc, out string staleReason)
            {
                staleReason = StaleReason;
                return Stale;
            }

            public DateTime GetNewestStoredMessageUtc() => DateTime.UtcNow;
            public DateTime GetNewestNonSelfStoredMessageUtc() => DateTime.UtcNow;
            public DateTime GetNewestGroupStoredMessageUtc() => DateTime.MinValue;

            public void MarkEnrichmentAwaitingHeavyHistory() => MarkedAwaiting = true;

            public HistoryCatchUpDrainAction DecideAfterFreshDrain(
                int offlineCount,
                int stagnantStop,
                out int stagnantCycles)
            {
                stagnantCycles = DrainAction == HistoryCatchUpDrainAction.Finish ? stagnantStop : 1;
                return DrainAction;
            }

            public Task RequestReconnectHistoryCatchUpAsync(string reason)
            {
                RequestedReason = reason;
                return Task.CompletedTask;
            }

            public void FinishHistoryCatchUpContinue(string reason, bool releaseEnrichment) =>
                FinishReason = reason;

            public void LogSyncObservedThisSession(DateTime syncUtc, string syncType)
            {
            }

            public void LogFreshnessPassed(
                string anyText,
                string nonSelfText,
                string groupText,
                int offlineCount,
                bool catchUpActive)
            {
            }

            public void LogCatchUpEmptyWhileTipFresh(int stagnantCycles, int stagnantStop)
            {
            }

            public void LogFreshnessNotStaleSkip(
                string staleReason,
                string newestText,
                string tipAgeMinutes,
                int offlineCount,
                bool catchUpActive)
            {
            }

            public void LogFreshnessStillStaleRequest(
                string staleReason,
                string newestText,
                int offlineCount)
            {
            }
        }
    }
}
