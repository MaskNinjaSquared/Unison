using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ForceHistoryRepairConsumeDispatchTests
    {
        [Fact]
        public async Task No_pending_is_noop()
        {
            var host = new FakeHost { Pending = false };
            await ForceHistoryRepairConsumeDispatch.RunAsync(host, "t");
            Assert.False(host.Requested);
        }

        [Fact]
        public async Task Failed_request_keeps_latch()
        {
            var host = new FakeHost { Pending = true, RequestOk = false };
            await ForceHistoryRepairConsumeDispatch.RunAsync(host, "t");
            Assert.True(host.Requested);
            Assert.False(host.Cleared);
        }

        [Fact]
        public async Task Successful_request_clears_latch()
        {
            var host = new FakeHost { Pending = true, RequestOk = true };
            await ForceHistoryRepairConsumeDispatch.RunAsync(host, "t");
            Assert.True(host.Cleared);
        }

        private sealed class FakeHost : IForceHistoryRepairConsumeHost
        {
            public bool Pending;
            public bool RequestOk;
            public bool Requested;
            public bool Cleared;

            public bool TryReadForceHistoryRepairPending() => Pending;

            public void LogForceHistoryRepair(string message)
            {
            }

            public Task<bool> RequestFullHistoryOnDemandTrackedAsync(
                string reason,
                bool isFreshnessRepair)
            {
                Requested = true;
                return Task.FromResult(RequestOk);
            }

            public void ClearForceHistoryRepairPending() => Cleared = true;
        }
    }
}
