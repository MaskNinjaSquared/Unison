using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class WaitForHistoryHydrationSettleDispatchTests
    {
        [Fact]
        public async Task Returns_when_in_flight_stays_zero()
        {
            var host = new FakeHost { InFlight = 0, Connected = true };
            await WaitForHistoryHydrationSettleDispatch.RunAsync(host);
            Assert.True(host.DelayCount >= 1);
        }

        [Fact]
        public async Task Waits_until_in_flight_clears()
        {
            var host = new FakeHost { InFlight = 2, Connected = true };
            _ = Task.Run(async () =>
            {
                await Task.Delay(15);
                host.InFlight = 0;
            });
            await WaitForHistoryHydrationSettleDispatch.RunAsync(host);
            Assert.Equal(0, host.InFlight);
        }

        private sealed class FakeHost : IWaitForHistoryHydrationSettleHost
        {
            public int InFlight;
            public bool Connected;
            public int DelayCount;

            public TimeSpan HydrationSettleBudget => TimeSpan.FromSeconds(1);

            public bool IsConnected => Connected;

            public int HistoryChatsInFlight => InFlight;

            public Task DelayAsync(TimeSpan delay)
            {
                DelayCount++;
                TimeSpan cap = TimeSpan.FromMilliseconds(20);
                TimeSpan wait = delay < cap ? delay : cap;
                return Task.Delay(wait);
            }
        }
    }
}
