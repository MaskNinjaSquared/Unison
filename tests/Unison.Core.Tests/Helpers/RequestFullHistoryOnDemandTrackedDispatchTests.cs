using System;
using System.Threading.Tasks;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class RequestFullHistoryOnDemandTrackedDispatchTests
    {
        [Fact]
        public async Task Returns_false_when_not_sent()
        {
            var host = new FakeHost
            {
                Result = new HistoryFullRequestResult
                {
                    Outcome = HistoryFullRequestOutcome.AlreadyClaimed
                }
            };
            Assert.False(await RequestFullHistoryOnDemandTrackedDispatch.RunAsync(host, "t", true));
            Assert.False(host.BannerBegun);
            Assert.False(host.Bounced);
        }

        [Fact]
        public async Task On_sent_freshness_repair_begins_banner_and_bounces()
        {
            var host = new FakeHost
            {
                Result = new HistoryFullRequestResult
                {
                    Outcome = HistoryFullRequestOutcome.Sent,
                    RequestId = "r1",
                    BaselineMessageCount = 3
                }
            };
            Assert.True(await RequestFullHistoryOnDemandTrackedDispatch.RunAsync(host, "go", true));
            Assert.True(host.BannerBegun);
            Assert.True(host.Bounced);
            Assert.True(host.Logged);
        }

        [Fact]
        public async Task On_sent_bootstrap_skips_banner()
        {
            var host = new FakeHost
            {
                Result = new HistoryFullRequestResult
                {
                    Outcome = HistoryFullRequestOutcome.Sent,
                    RequestId = "r1",
                    BaselineMessageCount = 0
                }
            };
            Assert.True(await RequestFullHistoryOnDemandTrackedDispatch.RunAsync(host, "boot", false));
            Assert.False(host.BannerBegun);
            Assert.True(host.Bounced);
        }

        private sealed class FakeHost : IRequestFullHistoryOnDemandTrackedHost
        {
            public HistoryFullRequestResult Result;
            public bool BannerBegun;
            public bool Bounced;
            public bool Logged;

            public TimeSpan FullHistoryCatchUpIdleAbort => TimeSpan.FromMinutes(1);
            public TimeSpan FullHistoryOnDemandResponseTimeout => TimeSpan.FromMinutes(5);

            public Task<HistoryFullRequestResult> RequestFullHistoryAsync(
                string reason,
                bool isFreshnessRepair,
                DateTime nowUtc) =>
                Task.FromResult(Result);

            public void LogFullHistoryRequested(
                string reason,
                string stanzaId,
                int baselineCount,
                bool isFreshnessRepair) =>
                Logged = true;

            public void WriteFullHistoryRequestedJournal(
                string reason,
                string stanzaId,
                int baselineCount,
                bool isFreshnessRepair)
            {
            }

            public void BeginHistoryCatchUpBanner(string reason) => BannerBegun = true;

            public void NotifyBackgroundHistorySyncBounced() => Bounced = true;
        }
    }
}
