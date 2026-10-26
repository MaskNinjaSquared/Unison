using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class FullHistoryOnDemandClearDispatchTests
    {
        [Fact]
        public void Tracked_ends_banner()
        {
            var host = new FakeHost { Result = HistoryOnDemandFullClear.Tracked };
            FullHistoryOnDemandClearDispatch.Run(host, "done");
            Assert.True(host.BannerEnded);
            Assert.Contains("Cleared full-history", host.LastLog);
        }

        [Fact]
        public void None_is_silent()
        {
            var host = new FakeHost { Result = HistoryOnDemandFullClear.Nothing };
            FullHistoryOnDemandClearDispatch.Run(host, "x");
            Assert.False(host.BannerEnded);
        }

        private sealed class FakeHost : IFullHistoryOnDemandClearHost
        {
            public HistoryOnDemandFullClear Result;
            public bool BannerEnded;
            public string LastLog;

            public HistoryOnDemandFullClear ClearFull(out string requestId, out string repairId)
            {
                requestId = "r1";
                repairId = "p1";
                return Result;
            }

            public void LogFullHistoryClear(string message) => LastLog = message;

            public void EndHistoryCatchUpBanner(string reason) => BannerEnded = true;
        }
    }
}
