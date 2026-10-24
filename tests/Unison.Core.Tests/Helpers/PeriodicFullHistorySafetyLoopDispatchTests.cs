using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class PeriodicFullHistorySafetyLoopDispatchTests
    {
        [Fact]
        public async Task Runs_tick_after_delay_until_cancelled()
        {
            var host = new FakeHost();
            using (var cts = new CancellationTokenSource())
            {
                cts.CancelAfter(TimeSpan.FromMilliseconds(30));
                await PeriodicFullHistorySafetyLoopDispatch.RunAsync(host, cts.Token);
            }

            Assert.True(host.TickCount >= 1);
        }

        [Fact]
        public async Task Tick_failure_is_logged_and_loop_continues()
        {
            var host = new FakeHost { ThrowOnTickOnce = true };
            using (var cts = new CancellationTokenSource())
            {
                cts.CancelAfter(TimeSpan.FromMilliseconds(40));
                await PeriodicFullHistorySafetyLoopDispatch.RunAsync(host, cts.Token);
            }

            Assert.NotNull(host.LoggedTickError);
            Assert.True(host.TickCount >= 2);
        }

        private sealed class FakeHost : IPeriodicFullHistorySafetyLoopHost
        {
            public int TickCount;
            public bool ThrowOnTickOnce;
            public string LoggedTickError;
            private bool _threw;

            public TimeSpan PeriodicFullHistorySafetyInterval => TimeSpan.FromMilliseconds(5);

            public Task DelayAsync(TimeSpan delay, CancellationToken token) =>
                Task.Delay(delay, token);

            public Task RunPeriodicFullHistorySafetyTickAsync()
            {
                TickCount++;
                if (ThrowOnTickOnce && !_threw)
                {
                    _threw = true;
                    throw new InvalidOperationException("tick");
                }

                return Task.CompletedTask;
            }

            public void LogPeriodicFullHistorySafetyTickFailed(string message) =>
                LoggedTickError = message;
        }
    }
}
