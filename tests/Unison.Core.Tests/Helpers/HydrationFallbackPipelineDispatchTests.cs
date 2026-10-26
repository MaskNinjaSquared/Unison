using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class HydrationFallbackPipelineDispatchTests
    {
        [Fact]
        public async Task Skips_when_tips_fresh()
        {
            var host = new FakeHost { StillStale = false };
            await HydrationFallbackPipelineDispatch.RunAsync(host, "t");
            Assert.False(host.BackfillStarted);
            Assert.False(host.DayRetryScheduled);
        }

        [Fact]
        public async Task Skips_when_hydration_already_active()
        {
            var host = new FakeHost { StillStale = true, HydrationActive = true };
            await HydrationFallbackPipelineDispatch.RunAsync(host, "t");
            Assert.False(host.BackfillStarted);
        }

        [Fact]
        public async Task Starts_backfill_and_retries_full_when_still_stale()
        {
            var host = new FakeHost { StillStale = true, BackfillCount = 2 };
            await HydrationFallbackPipelineDispatch.RunAsync(host, "go");
            Assert.True(host.BackfillStarted);
            Assert.True(host.DayAttemptNoted);
            Assert.True(host.ReconnectFired);
            Assert.True(host.DayRetryScheduled);
        }

        private sealed class FakeHost : IHydrationFallbackPipelineHost
        {
            public bool StillStale;
            public bool HydrationActive;
            public int BackfillCount;
            public bool BackfillStarted;
            public bool DayAttemptNoted;
            public bool ReconnectFired;
            public bool DayRetryScheduled;

            public bool IsHistoryHydrationActive => HydrationActive;

            public bool TryGetFreshnessStaleReason(DateTime nowUtc, out string staleReason)
            {
                staleReason = StillStale ? "tip-old" : null;
                return StillStale;
            }

            public void NoteHydrationDayAttempt(DateTime nowUtc) => DayAttemptNoted = true;

            public void WriteHydrationFallbackStart(string reason, string staleReason)
            {
            }

            public Task<int> StartBackgroundHistoryBackfillAsync(string reason)
            {
                BackfillStarted = true;
                return Task.FromResult(BackfillCount);
            }

            public void FireAndForgetReconnectHistoryCatchUp(string reason) =>
                ReconnectFired = true;

            public void ScheduleHydrationDayRetryIfNeeded(string reason) =>
                DayRetryScheduled = true;

            public void LogHydrationFallback(string message)
            {
            }

            public void RecordHydrationFallbackException(Exception error, string reason)
            {
            }
        }
    }
}
