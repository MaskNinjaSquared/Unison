using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ConnectionHealthMonitorTests
    {
        [Fact]
        public async Task Stops_when_socket_disconnects()
        {
            var host = new FakeHost { Connected = false };
            using (var cts = new CancellationTokenSource())
            {
                await ConnectionHealthMonitor.RunDeferAwareAsync(host, cts.Token);
            }

            Assert.Equal(0, host.SoftReconnects);
            Assert.Contains("disconnected", host.LastLog);
        }

        [Fact]
        public async Task Force_reconnects_when_stalled_and_not_deferred()
        {
            var host = new FakeHost
            {
                Connected = true,
                Stalled = true,
                Fresh = false
            };
            using (var cts = new CancellationTokenSource())
            {
                // First Delay then tick — cancel after first reconnect scheduling.
                host.OnSoftReconnect = () => cts.Cancel();
                try
                {
                    await ConnectionHealthMonitor.RunDeferAwareAsync(host, cts.Token);
                }
                catch (TaskCanceledException)
                {
                }
            }

            Assert.Equal(1, host.Disconnects);
            Assert.Equal(1, host.SoftReconnects);
        }

        [Fact]
        public async Task Defers_force_reconnect_when_usync_held()
        {
            var host = new FakeHost
            {
                Connected = true,
                Stalled = false,
                Fresh = false,
                UsyncHeld = true,
                ProbeResult = false,
                MaxTicks = 2
            };
            using (var cts = new CancellationTokenSource())
            {
                host.OnTickLimit = () => cts.Cancel();
                try
                {
                    await ConnectionHealthMonitor.RunDeferAwareAsync(host, cts.Token);
                }
                catch (TaskCanceledException)
                {
                }
            }

            Assert.Equal(0, host.Disconnects);
            Assert.Contains("health-defer-usync-held", host.LastJournal);
        }

        private sealed class FakeHost : IConnectionHealthMonitorHost
        {
            public bool Connected { get; set; } = true;
            public bool Stalled { get; set; }
            public bool Fresh { get; set; } = true;
            public bool UsyncHeld { get; set; }
            public bool ProbeResult { get; set; } = true;
            public int MaxTicks { get; set; }
            public int SoftReconnects { get; private set; }
            public int Disconnects { get; private set; }
            public string LastLog { get; private set; } = string.Empty;
            public string LastJournal { get; private set; } = string.Empty;
            public Action OnSoftReconnect { get; set; }
            public Action OnTickLimit { get; set; }
            private int _ticks;

            public bool ShouldAbortLoop => false;
            public bool IsMonitoredSocketCurrent => true;
            public bool IsMonitoredSocketConnected => Connected;
            public bool IsNodeProcessingStalled => Stalled;
            public bool HasFreshBackgroundConnection => Fresh;
            public bool IsUsyncGateHeld => UsyncHeld;
            public bool IsHistoryCatchUpActive => false;
            public int QueuedNodeProcessingCount => Stalled ? 9 : 0;
            public int PendingQueryCount => 0;

            public Task DelayCheckIntervalAsync(CancellationToken token)
            {
                _ticks++;
                if (MaxTicks > 0 && _ticks >= MaxTicks)
                {
                    OnTickLimit?.Invoke();
                }

                return Task.CompletedTask;
            }

            public void RestartIncomingPumpIfStalled()
            {
            }

            public Task<bool> ProbeBackgroundAsync() => Task.FromResult(ProbeResult);

            public void ForceDisconnectMonitoredSocket() => Disconnects++;

            public void RequestSoftReconnect(string reason)
            {
                SoftReconnects++;
                OnSoftReconnect?.Invoke();
            }

            public void WriteHealthJournal(string eventName, string detail) =>
                LastJournal = eventName;

            public void LogHealth(string message) => LastLog = message;

            public void RecordHealthException(Exception error)
            {
            }

            public string TryGetExplicitLogoutStreamCode(Exception error) => null;

            public void LatchFatalAndReportStream(string code)
            {
            }
        }
    }
}
