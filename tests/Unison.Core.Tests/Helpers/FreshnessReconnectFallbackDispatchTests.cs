using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class FreshnessReconnectFallbackDispatchTests
    {
        [Fact]
        public void Skip_in_progress_does_not_start_work()
        {
            var host = new FakeHost { InProgress = true, Registered = true, TipsStale = true };
            FreshnessReconnectFallbackDispatch.Schedule(host, "t");

            Assert.False(host.HydrationStarted);
            Assert.False(host.FireAndForgetCalled);
            Assert.Contains("already in progress", host.LastLog);
        }

        [Fact]
        public void Hydrate_on_cooldown_starts_pipeline()
        {
            var host = new FakeHost
            {
                Registered = true,
                TipsStale = true,
                LastFallbackUtc = DateTime.UtcNow,
                Cooldown = TimeSpan.FromMinutes(20)
            };
            FreshnessReconnectFallbackDispatch.Schedule(host, "cool");

            Assert.True(host.HydrationStarted);
            Assert.False(host.FireAndForgetCalled);
        }

        [Fact]
        public void Start_reconnect_persists_and_fires_delayed_work()
        {
            var host = new FakeHost
            {
                Registered = true,
                TipsStale = true,
                LastFallbackUtc = DateTime.MinValue
            };
            FreshnessReconnectFallbackDispatch.Schedule(host, "go");

            Assert.True(host.Persisted);
            Assert.True(host.InProgress);
            Assert.True(host.FireAndForgetCalled);
        }

        [Fact]
        public async Task After_delay_abort_when_tips_fresh()
        {
            var host = new FakeHost
            {
                Registered = true,
                TipsStale = false,
                InProgress = true
            };
            await FreshnessReconnectFallbackDispatch.RunReconnectAfterDelayAsync(host, "t");

            Assert.False(host.SocketTornDown);
            Assert.False(host.InProgress);
            Assert.Contains("became fresh", host.LastLog);
        }

        [Fact]
        public async Task After_delay_reconnects_when_still_stale()
        {
            var host = new FakeHost
            {
                Registered = true,
                TipsStale = true,
                InProgress = true
            };
            await FreshnessReconnectFallbackDispatch.RunReconnectAfterDelayAsync(host, "t");

            Assert.True(host.SocketTornDown);
            Assert.True(host.AutoReconnectScheduled);
            Assert.False(host.InProgress);
        }

        private sealed class FakeHost : IFreshnessReconnectFallbackHost
        {
            public bool InProgress;
            public bool Registered;
            public bool TipsStale;
            public DateTime LastFallbackUtc = DateTime.MinValue;
            public TimeSpan Cooldown = TimeSpan.FromMinutes(20);
            public bool Persisted;
            public bool HydrationStarted;
            public bool FireAndForgetCalled;
            public bool SocketTornDown;
            public bool AutoReconnectScheduled;
            public string LastLog;

            public bool FreshnessReconnectFallbackInProgress
            {
                get => InProgress;
                set => InProgress = value;
            }

            public bool SuppressReconnect => false;
            public bool IsAuthRegistered => Registered;
            public DateTime LastFreshnessReconnectFallbackUtc => LastFallbackUtc;
            public TimeSpan FreshnessReconnectFallbackCooldown => Cooldown;

            public TimeSpan FreshnessReconnectDelay => TimeSpan.Zero;

            public bool TryGetFreshnessStaleReason(DateTime nowUtc, out string staleReason)
            {
                staleReason = TipsStale ? "tip-old" : null;
                return TipsStale;
            }

            public void PersistFreshnessReconnectFallbackUtc(DateTime utc) => Persisted = true;

            public void LogFreshnessFallback(string message) => LastLog = message;

            public void StartHydrationFallbackPipeline(string reason) => HydrationStarted = true;

            public void PublishFreshnessReconnectStatus(string status)
            {
            }

            public void StopConnectionHealthMonitor(string reason)
            {
            }

            public void TearDownSocketForReconnect() => SocketTornDown = true;

            public void ScheduleAutoReconnect(string reason) => AutoReconnectScheduled = true;

            public void RaiseFreshnessFallbackError(Exception error)
            {
            }

            public void FireAndForgetFreshnessReconnect(Func<Task> work) =>
                FireAndForgetCalled = true;
        }
    }
}
