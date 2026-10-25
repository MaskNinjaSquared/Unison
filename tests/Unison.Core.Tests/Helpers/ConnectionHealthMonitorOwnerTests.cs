using System.Threading;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Connection;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ConnectionHealthMonitorOwnerTests
    {
        [Fact]
        public void Start_arms_health_and_periodic_and_runs_health_loop()
        {
            var io = new RecordingIoHost();
            var owner = new ConnectionHealthMonitorOwner(io);

            owner.Start(new object());

            Assert.True(owner.IsHealthMonitorArmed);
            Assert.True(owner.IsPeriodicSafetyArmed);
            Assert.True(io.WaitHealthStarted(2000));
            Assert.Equal(1, io.HealthStarts);
            Assert.Equal(1, io.PeriodicStarts);
        }

        [Fact]
        public void Stop_disarms_both_and_second_stop_is_noop()
        {
            var io = new RecordingIoHost();
            var owner = new ConnectionHealthMonitorOwner(io);

            owner.Start(new object());
            Assert.True(io.WaitHealthStarted(2000));

            owner.Stop("test");
            Assert.False(owner.IsHealthMonitorArmed);
            Assert.False(owner.IsPeriodicSafetyArmed);

            owner.Stop("again");
            Assert.False(owner.IsHealthMonitorArmed);
        }

        [Fact]
        public void Start_null_socket_returns_after_stop_without_arming()
        {
            // Dispatch: Stop → (socket null) return — no loop, no periodic.
            var io = new RecordingIoHost();
            var owner = new ConnectionHealthMonitorOwner(io);

            owner.Start(new object());
            Assert.True(io.WaitHealthStarted(2000));

            owner.Start(null);

            Assert.False(owner.IsHealthMonitorArmed);
            Assert.False(owner.IsPeriodicSafetyArmed);
            Assert.Equal(1, io.HealthStarts);
        }

        [Fact]
        public void Restart_cancels_prior_health_loop()
        {
            var io = new RecordingIoHost();
            var owner = new ConnectionHealthMonitorOwner(io);

            owner.Start(new object());
            Assert.True(io.WaitHealthStarted(2000));
            CancellationToken first = io.LastHealthToken;

            owner.Start(new object());
            Assert.True(io.WaitHealthStarted(2000, minStarts: 2));

            Assert.True(first.IsCancellationRequested);
            Assert.Equal(2, io.HealthStarts);
            Assert.True(owner.IsHealthMonitorArmed);
        }

        private sealed class RecordingIoHost : IConnectionHealthMonitorIoHost
        {
            private readonly object _gate = new object();
            private int _healthStarts;
            private int _periodicStarts;
            private CancellationToken _lastHealthToken;

            public int HealthStarts
            {
                get { lock (_gate) return _healthStarts; }
            }

            public int PeriodicStarts
            {
                get { lock (_gate) return _periodicStarts; }
            }

            public CancellationToken LastHealthToken
            {
                get { lock (_gate) return _lastHealthToken; }
            }

            public bool WaitHealthStarted(int timeoutMs, int minStarts = 1)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    if (HealthStarts >= minStarts)
                    {
                        return true;
                    }

                    Thread.Sleep(10);
                }

                return HealthStarts >= minStarts;
            }

            public async Task RunHealthLoopAsync(object socket, CancellationToken token)
            {
                lock (_gate)
                {
                    _healthStarts++;
                    _lastHealthToken = token;
                }

                try
                {
                    await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                }
            }

            public async Task RunPeriodicFullHistorySafetyLoopAsync(CancellationToken token)
            {
                lock (_gate)
                {
                    _periodicStarts++;
                }

                try
                {
                    await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                }
            }
        }
    }
}
