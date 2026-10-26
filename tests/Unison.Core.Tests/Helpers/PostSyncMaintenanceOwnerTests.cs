using System.Threading;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Connection;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class PostSyncMaintenanceOwnerTests
    {
        [Fact]
        public void SchedulePostReplay_runs_io_and_arms_cts()
        {
            var io = new RecordingIoHost();
            var owner = new PostSyncMaintenanceOwner(io);

            owner.SchedulePostReplay(2);

            Assert.True(io.WaitPostReplay(2000));
            Assert.Equal(1, io.PostReplayRuns);
            Assert.Equal(2, io.LastOfflineCount);
        }

        [Fact]
        public void SchedulePostReplay_cancels_previous_token()
        {
            var io = new RecordingIoHost { HoldPostReplay = true };
            var owner = new PostSyncMaintenanceOwner(io);

            owner.SchedulePostReplay(1);
            Assert.True(io.WaitPostReplay(2000));
            CancellationToken first = io.LastPostReplayToken;

            owner.SchedulePostReplay(2);
            Assert.True(io.WaitPostReplay(2000, minRuns: 2));

            Assert.True(first.IsCancellationRequested);
            Assert.Equal(2, io.PostReplayRuns);
        }

        [Fact]
        public void Enrichment_claims_once_per_session_until_reset()
        {
            var io = new RecordingIoHost();
            var owner = new PostSyncMaintenanceOwner(io);

            owner.ScheduleEnrichment("first");
            Assert.True(io.WaitEnrichment(2000));
            Assert.Equal(1, io.EnrichmentRuns);
            Assert.True(owner.HasStartedEnrichment);

            owner.ScheduleEnrichment("second");
            Thread.Sleep(50);
            Assert.Equal(1, io.EnrichmentRuns);
            Assert.Equal(1, io.AlreadyStartedLogs);

            owner.ResetEnrichmentStarted();
            Assert.False(owner.HasStartedEnrichment);

            owner.ScheduleEnrichment("after-reset");
            Assert.True(io.WaitEnrichment(2000, minRuns: 2));
            Assert.Equal(2, io.EnrichmentRuns);
        }

        [Fact]
        public void CancelAll_cancels_in_flight_post_replay()
        {
            var io = new RecordingIoHost { HoldPostReplay = true };
            var owner = new PostSyncMaintenanceOwner(io);

            owner.SchedulePostReplay(1);
            Assert.True(io.WaitPostReplay(2000));
            CancellationToken token = io.LastPostReplayToken;

            owner.CancelAll();

            Assert.True(token.IsCancellationRequested);
            Assert.False(owner.IsPostReplayArmed);
        }

        private sealed class RecordingIoHost : IPostSyncMaintenanceIoHost
        {
            private readonly object _gate = new object();
            private int _postReplayRuns;
            private int _enrichmentRuns;
            private int _alreadyStartedLogs;
            private int _lastOffline;
            private CancellationToken _lastPostReplayToken;

            public bool HoldPostReplay { get; set; }

            public int PostReplayRuns
            {
                get { lock (_gate) return _postReplayRuns; }
            }

            public int EnrichmentRuns
            {
                get { lock (_gate) return _enrichmentRuns; }
            }

            public int AlreadyStartedLogs
            {
                get { lock (_gate) return _alreadyStartedLogs; }
            }

            public int LastOfflineCount
            {
                get { lock (_gate) return _lastOffline; }
            }

            public CancellationToken LastPostReplayToken
            {
                get { lock (_gate) return _lastPostReplayToken; }
            }

            public bool WaitPostReplay(int timeoutMs, int minRuns = 1)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    if (PostReplayRuns >= minRuns)
                    {
                        return true;
                    }

                    Thread.Sleep(10);
                }

                return PostReplayRuns >= minRuns;
            }

            public bool WaitEnrichment(int timeoutMs, int minRuns = 1)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    if (EnrichmentRuns >= minRuns)
                    {
                        return true;
                    }

                    Thread.Sleep(10);
                }

                return EnrichmentRuns >= minRuns;
            }

            public async Task RunPostReplayMaintenanceAsync(
                int offlineCount,
                CancellationToken token)
            {
                lock (_gate)
                {
                    _postReplayRuns++;
                    _lastOffline = offlineCount;
                    _lastPostReplayToken = token;
                }

                if (HoldPostReplay)
                {
                    try
                    {
                        await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                    }
                    catch (TaskCanceledException)
                    {
                    }
                }
            }

            public Task RunPostMessageEnrichmentAsync(
                string reason,
                CancellationToken token)
            {
                lock (_gate)
                {
                    _enrichmentRuns++;
                }

                return Task.CompletedTask;
            }

            public void ClearEnrichmentAwaitingHeavyHistory()
            {
            }

            public void LogPostMessageEnrichment(string message)
            {
                if (message != null &&
                    message.IndexOf("already started", System.StringComparison.Ordinal) >= 0)
                {
                    lock (_gate)
                    {
                        _alreadyStartedLogs++;
                    }
                }
            }

            public void WritePostMessageEnrichmentScheduled(string reason)
            {
            }
        }
    }
}
