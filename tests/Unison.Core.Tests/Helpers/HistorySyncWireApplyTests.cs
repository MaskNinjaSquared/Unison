using System.Collections.Generic;
using System.Threading.Tasks;
using Proto;
using Unison.Core.Contracts.WhatsApp;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class HistorySyncWireApplyTests
    {
        [Theory]
        [InlineData(true, true, true)]
        [InlineData(false, false, true)]
        [InlineData(true, false, true)]
        [InlineData(false, true, false)]
        public void ShouldDrop_when_fatal_or_no_auth(
            bool fatal,
            bool hasAuth,
            bool expectDrop)
        {
            Assert.Equal(expectDrop, HistorySyncWireApply.ShouldDrop(fatal, hasAuth));
        }

        [Fact]
        public void Run_drops_when_no_auth()
        {
            var host = new FakeHost { HasAuthState = false };
            HistorySyncWireApply.Run(host, new HistorySync());
            Assert.Empty(host.Log);
        }

        [Fact]
        public void Run_applies_and_writes_begin_end()
        {
            var host = new FakeHost { HasAuthState = true };
            var sync = new HistorySync();

            HistorySyncWireApply.Run(host, sync);

            Assert.Equal(2, host.Log.Count);
            Assert.StartsWith("begin:", host.Log[0]);
            Assert.StartsWith("end:", host.Log[1]);
            Assert.Equal(1, host.ApplyCount);
        }

        private sealed class FakeHost : IHistorySyncWireHost
        {
            public List<string> Log { get; } = new List<string>();

            public int ApplyCount { get; private set; }

            public bool FatalSessionEnded { get; set; }

            public bool HasAuthState { get; set; }

            public void WriteSyncApplyBegin(int conversations, string syncType) =>
                Log.Add("begin:" + conversations + ":" + syncType);

            public void WriteSyncApplyEnd(int conversations, long elapsedMs) =>
                Log.Add("end:" + conversations + ":" + elapsedMs);

            public Task ApplyHistorySyncAsync(HistorySync sync)
            {
                ApplyCount++;
                return Task.CompletedTask;
            }
        }
    }
}
