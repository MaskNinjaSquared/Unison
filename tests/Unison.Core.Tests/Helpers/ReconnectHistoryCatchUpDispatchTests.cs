using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ReconnectHistoryCatchUpDispatchTests
    {
        [Fact]
        public async Task Floor_abort_releases_enrichment_when_cold()
        {
            var host = new FakeHost
            {
                Quiet = false,
                Connected = true,
                CatchUpActive = false,
                WindowVisible = true
            };
            await ReconnectHistoryCatchUpDispatch.RunAsync(host, "t");
            Assert.True(host.EnrichmentCleared);
            Assert.Equal("catchup-floor-aborted:t", host.EnrichmentReason);
            Assert.False(host.Requested);
        }

        [Fact]
        public async Task Floor_abort_keeps_enrichment_mid_catch_up()
        {
            var host = new FakeHost
            {
                Quiet = false,
                Connected = true,
                CatchUpActive = true,
                WindowVisible = true
            };
            await ReconnectHistoryCatchUpDispatch.RunAsync(host, "t");
            Assert.False(host.EnrichmentCleared);
            Assert.False(host.Requested);
        }

        [Fact]
        public async Task Proceed_requests_full_history()
        {
            var host = new FakeHost
            {
                Quiet = true,
                Connected = true,
                CatchUpActive = true,
                WindowVisible = false,
                RequestOk = true
            };
            await ReconnectHistoryCatchUpDispatch.RunAsync(host, "go");
            Assert.True(host.ReleasedUntracked);
            Assert.True(host.Requested);
            Assert.False(host.EnrichmentCleared);
        }

        [Fact]
        public async Task Failed_request_releases_enrichment()
        {
            var host = new FakeHost
            {
                Quiet = true,
                Connected = true,
                CatchUpActive = false,
                WindowVisible = true,
                RequestOk = false
            };
            await ReconnectHistoryCatchUpDispatch.RunAsync(host, "fail");
            Assert.True(host.EnrichmentCleared);
            Assert.Equal("catchup-failed:fail", host.EnrichmentReason);
        }

        private sealed class FakeHost : IReconnectHistoryCatchUpHost
        {
            public bool Quiet;
            public bool Connected;
            public bool CatchUpActive;
            public bool WindowVisible;
            public bool RequestOk;
            public bool ReleasedUntracked;
            public bool Requested;
            public bool EnrichmentCleared;
            public string EnrichmentReason;

            public bool IsHistoryCatchUpActive => CatchUpActive;
            public bool IsConnected => Connected;
            public bool IsWindowVisible => WindowVisible;

            public Task<bool> WaitForStartupQuietAsync(
                TimeSpan timeout,
                string reason,
                CancellationToken token) =>
                Task.FromResult(Quiet);

            public void ReleaseFullHistoryIfUntracked() => ReleasedUntracked = true;

            public Task<bool> RequestFullHistoryOnDemandTrackedAsync(
                string reason,
                bool isFreshnessRepair)
            {
                Requested = true;
                return Task.FromResult(RequestOk);
            }

            public void ClearEnrichmentAwaitingHeavyHistory() => EnrichmentCleared = true;

            public void TrySchedulePostMessageEnrichment(string reason) =>
                EnrichmentReason = reason;

            public void LogReconnectHistoryCatchUp(string message)
            {
            }

            public void WriteReconnectHistoryCatchUpJournal(string reason)
            {
            }
        }
    }
}
