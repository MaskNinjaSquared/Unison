using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class BackgroundResolutionPassTests
    {
        [Fact]
        public async Task Runs_the_contact_body_when_every_gate_is_clear()
        {
            var gate = new FakeGate();
            int runs = 0;
            var pass = new BackgroundResolutionPass(
                gate,
                new FakeDeferred(),
                _ =>
                {
                    Interlocked.Increment(ref runs);
                    return Task.CompletedTask;
                });

            await pass.RunAsync(CancellationToken.None);

            Assert.Equal(1, runs);
            Assert.Equal(1, gate.QuietWaits);
            Assert.Equal(1, gate.TracePhases);
        }

        [Fact]
        public async Task Skips_when_quiet_wait_is_cancelled()
        {
            var gate = new FakeGate { QuietResult = false };
            int runs = 0;
            var pass = new BackgroundResolutionPass(
                gate,
                new FakeDeferred(),
                _ =>
                {
                    Interlocked.Increment(ref runs);
                    return Task.CompletedTask;
                });

            await pass.RunAsync(CancellationToken.None);

            Assert.Equal(0, runs);
            Assert.Equal(1, gate.ClearCount);
        }

        [Fact]
        public async Task Skips_when_handshake_is_not_ready()
        {
            var gate = new FakeGate { HandshakeReady = false };
            int runs = 0;
            var pass = new BackgroundResolutionPass(
                gate,
                new FakeDeferred(),
                _ =>
                {
                    Interlocked.Increment(ref runs);
                    return Task.CompletedTask;
                });

            await pass.RunAsync(CancellationToken.None);

            Assert.Equal(0, runs);
            Assert.Equal(1, gate.ClearCount);
        }

        [Fact]
        public async Task Skips_when_replay_drain_is_active()
        {
            var gate = new FakeGate { DeferReplay = true };
            int runs = 0;
            var pass = new BackgroundResolutionPass(
                gate,
                new FakeDeferred(),
                _ =>
                {
                    Interlocked.Increment(ref runs);
                    return Task.CompletedTask;
                });

            await pass.RunAsync(CancellationToken.None);

            Assert.Equal(0, runs);
            Assert.Equal(1, gate.ClearCount);
        }

        [Fact]
        public async Task Marks_heavy_history_and_does_not_clear_status_when_catch_up_is_active()
        {
            var gate = new FakeGate { HistoryCatchUpActive = true };
            int runs = 0;
            var pass = new BackgroundResolutionPass(
                gate,
                new FakeDeferred(),
                _ =>
                {
                    Interlocked.Increment(ref runs);
                    return Task.CompletedTask;
                });

            await pass.RunAsync(CancellationToken.None);

            Assert.Equal(0, runs);
            Assert.Equal(1, gate.HeavyHistoryMarks);
            Assert.Equal(1, gate.DeferredCatchUpWrites);
            Assert.Equal(0, gate.ClearCount);
        }

        [Fact]
        public async Task Schedules_deferred_avatars_when_sync_traffic_blocks_fetch()
        {
            var gate = new FakeGate { AvatarDeferReason = "on-demand" };
            var deferred = new FakeDeferred();
            int runs = 0;
            var pass = new BackgroundResolutionPass(
                gate,
                deferred,
                _ =>
                {
                    Interlocked.Increment(ref runs);
                    return Task.CompletedTask;
                });

            await pass.RunAsync(CancellationToken.None);

            Assert.Equal(0, runs);
            Assert.Equal("on-demand", deferred.LastReason);
            Assert.Equal(1, gate.ClearCount);
        }

        private sealed class FakeDeferred : IDeferredAvatarResolution
        {
            public string LastReason { get; private set; }

            public void Schedule(string reason, TimeSpan? delay = null) => LastReason = reason;

            public void Cancel()
            {
            }

            public void ResetScheduleStreak()
            {
            }
        }

        private sealed class FakeGate : IBackgroundResolutionGate
        {
            public bool QuietResult { get; set; } = true;
            public bool HandshakeReady { get; set; } = true;
            public bool DeferReplay { get; set; }
            public bool HistoryCatchUpActive { get; set; }
            public string AvatarDeferReason { get; set; }

            public int QuietWaits { get; private set; }
            public int ClearCount { get; private set; }
            public int HeavyHistoryMarks { get; private set; }
            public int DeferredCatchUpWrites { get; private set; }
            public int TracePhases { get; private set; }

            public bool IsHandshakeReady => HandshakeReady;
            public bool ShouldDeferReplay => DeferReplay;
            public bool IsHistoryCatchUpActive => HistoryCatchUpActive;

            public Task<bool> WaitForQuietAsync(
                TimeSpan budget,
                string reason,
                CancellationToken token)
            {
                QuietWaits++;
                return Task.FromResult(QuietResult);
            }

            public bool ShouldDeferAvatarFetch(out string reason)
            {
                reason = AvatarDeferReason;
                return AvatarDeferReason != null;
            }

            public void MarkEnrichmentAwaitingHeavyHistory() => HeavyHistoryMarks++;

            public void ClearSyncStatus() => ClearCount++;

            public void PublishClearedSyncStatus() => ClearCount++;

            public IDisposable TracePhase(string phase)
            {
                TracePhases++;
                return new Noop();
            }

            public void WriteDeferredCatchUp(string reason) => DeferredCatchUpWrites++;

            private sealed class Noop : IDisposable
            {
                public void Dispose()
                {
                }
            }
        }
    }
}
