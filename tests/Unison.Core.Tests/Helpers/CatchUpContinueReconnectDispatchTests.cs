using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class CatchUpContinueReconnectDispatchTests
    {
        [Fact]
        public void Busy_finishes_catch_up()
        {
            var host = new FakeHost { InProgress = true, Registered = true };
            CatchUpContinueReconnectDispatch.Schedule(host, "t");

            Assert.Equal("continue-busy", host.FinishReason);
            Assert.False(host.FireAndForgetCalled);
        }

        [Fact]
        public void Start_claims_slot_and_fires_delayed_work()
        {
            var host = new FakeHost { Registered = true };
            CatchUpContinueReconnectDispatch.Schedule(host, "go");

            Assert.True(host.InProgress);
            Assert.True(host.EnrichmentMarked);
            Assert.True(host.FireAndForgetCalled);
            Assert.Null(host.FinishReason);
        }

        [Fact]
        public async Task After_delay_aborts_when_tip_fresh_and_catch_up_idle()
        {
            var host = new FakeHost
            {
                Registered = true,
                InProgress = true,
                TipStale = false,
                CatchUpActive = false
            };
            await CatchUpContinueReconnectDispatch.RunReconnectAfterDelayAsync(host, "t");

            Assert.Equal("fresh-before-continue", host.FinishReason);
            Assert.False(host.SocketTornDown);
            Assert.False(host.InProgress);
        }

        [Fact]
        public async Task After_delay_reconnects_while_catch_up_active()
        {
            var host = new FakeHost
            {
                Registered = true,
                InProgress = true,
                TipStale = false,
                CatchUpActive = true
            };
            await CatchUpContinueReconnectDispatch.RunReconnectAfterDelayAsync(host, "t");

            Assert.True(host.SocketTornDown);
            Assert.True(host.AutoReconnectScheduled);
            Assert.Null(host.FinishReason);
            Assert.False(host.InProgress);
        }

        private sealed class FakeHost : ICatchUpContinueReconnectHost
        {
            public bool InProgress;
            public bool Registered;
            public bool TipStale = true;
            public bool CatchUpActive;
            public bool EnrichmentMarked;
            public bool FireAndForgetCalled;
            public bool SocketTornDown;
            public bool AutoReconnectScheduled;
            public string FinishReason;
            public DateTime LastContinue;

            public bool FreshnessReconnectFallbackInProgress
            {
                get => InProgress;
                set => InProgress = value;
            }

            public bool SuppressReconnect => false;
            public bool IsAuthRegistered => Registered;
            public DateTime LastCatchUpContinueUtc
            {
                get => LastContinue;
                set => LastContinue = value;
            }

            public TimeSpan CatchUpContinueReconnectCooldown => TimeSpan.FromSeconds(15);
            public TimeSpan CatchUpContinueReconnectDelay => TimeSpan.Zero;
            public int CatchUpRound => 1;
            public bool IsHistoryCatchUpBannerActive => false;

            public bool IsHistoryCatchUpActive() => CatchUpActive;

            public bool TryGetFreshnessStaleReason(DateTime nowUtc, out string staleReason)
            {
                staleReason = TipStale ? "old" : null;
                return TipStale;
            }

            public void FinishHistoryCatchUpContinue(string reason, bool releaseEnrichment) =>
                FinishReason = reason;

            public void MarkEnrichmentAwaitingHeavyHistory() => EnrichmentMarked = true;

            public void BeginHistoryCatchUpBanner(string reason)
            {
            }

            public void PublishHistoryCatchUpStatus()
            {
            }

            public void WriteCatchUpContinueJournal(string triggerReason, int delayMs)
            {
            }

            public void LogCatchUpContinue(string message)
            {
            }

            public void StopConnectionHealthMonitor(string reason)
            {
            }

            public void TearDownSocketForReconnect() => SocketTornDown = true;

            public void ScheduleAutoReconnect(string reason) => AutoReconnectScheduled = true;

            public void RecordCatchUpContinueException(Exception error, string triggerReason)
            {
            }

            public void FireAndForgetCatchUpContinue(Func<Task> work) =>
                FireAndForgetCalled = true;
        }
    }
}
