using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class UserResyncFullHistoryDispatchTests
    {
        [Fact]
        public async Task First_success_skips_fresh_retry()
        {
            var host = new FakeHost { FirstResult = true };
            bool ok = await UserResyncFullHistoryDispatch.RunAsync(host, "test");

            Assert.True(ok);
            Assert.Equal(1, host.RequestCount);
            Assert.False(host.ReleasedGate);
            Assert.False(host.ConsumedForceRepair);
        }

        [Fact]
        public async Task Failed_first_retries_on_fresh_transport()
        {
            var host = new FakeHost { FirstResult = false, SecondResult = true };
            bool ok = await UserResyncFullHistoryDispatch.RunAsync(host, "test");

            Assert.True(ok);
            Assert.Equal(2, host.RequestCount);
            Assert.True(host.ReleasedGate);
            Assert.True(host.FreshConnectUsed);
            Assert.False(host.ConsumedForceRepair);
        }

        [Fact]
        public async Task Both_fail_consumes_force_repair_and_uses_claim()
        {
            var host = new FakeHost
            {
                FirstResult = false,
                SecondResult = false,
                FullClaimed = true
            };
            bool ok = await UserResyncFullHistoryDispatch.RunAsync(host, "test");

            Assert.True(ok);
            Assert.True(host.ConsumedForceRepair);
        }

        private sealed class FakeHost : IUserResyncFullHistoryHost
        {
            public bool FirstResult;
            public bool SecondResult;
            public bool FullClaimed;
            public int RequestCount;
            public bool ReleasedGate;
            public bool FreshConnectUsed;
            public bool ConsumedForceRepair;

            public bool IsFullHistoryClaimed => FullClaimed;

            public Task EnsureConnectedAsync(int timeoutMs, bool forceFreshTransport)
            {
                if (forceFreshTransport)
                {
                    FreshConnectUsed = true;
                }

                return Task.CompletedTask;
            }

            public Task<bool> RequestFullHistoryOnDemandTrackedAsync(
                string reason,
                bool isFreshnessRepair)
            {
                RequestCount++;
                return Task.FromResult(RequestCount == 1 ? FirstResult : SecondResult);
            }

            public void ReleaseFullHistoryRequestGate() => ReleasedGate = true;

            public Task TryConsumeMessageStoreForceHistoryRepairAsync(string reason)
            {
                ConsumedForceRepair = true;
                return Task.CompletedTask;
            }

            public void LogUserResync(string message)
            {
            }

            public void RaiseUserResyncStatus(string status)
            {
            }
        }
    }
}
