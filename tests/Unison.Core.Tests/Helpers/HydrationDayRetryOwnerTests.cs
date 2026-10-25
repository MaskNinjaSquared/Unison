using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Connection;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class HydrationDayRetryOwnerTests
    {
        [Fact]
        public void NoteAttempt_resets_on_new_day_and_increments()
        {
            var io = new RecordingIoHost();
            var owner = new HydrationDayRetryOwner(io);
            DateTime day1 = new DateTime(2026, 10, 25, 8, 0, 0, DateTimeKind.Utc);

            owner.NoteAttempt(day1);
            Assert.Equal(1, owner.DayAttempts);
            Assert.Equal(day1.Date, owner.DayAnchorUtc);
            Assert.Equal(1, io.StampCalls);

            owner.NoteAttempt(day1.AddHours(2));
            Assert.Equal(2, owner.DayAttempts);

            owner.NoteAttempt(day1.AddDays(1));
            Assert.Equal(1, owner.DayAttempts);
            Assert.Equal(day1.AddDays(1).Date, owner.DayAnchorUtc);
        }

        [Fact]
        public void ScheduleIfNeeded_skips_when_not_stale()
        {
            var io = new RecordingIoHost { StillStale = false };
            var owner = new HydrationDayRetryOwner(io);

            owner.ScheduleIfNeeded("test");

            Assert.False(owner.IsArmed);
            Assert.Equal(0, io.PipelineRuns);
        }

        [Fact]
        public void ScheduleIfNeeded_arms_when_stale_and_under_cap()
        {
            var io = new RecordingIoHost { StillStale = true };
            var owner = new HydrationDayRetryOwner(io);

            // Day attempts start at 0 — ShouldArm(true, 0, 5) is true.
            owner.ScheduleIfNeeded("stale");

            Assert.True(owner.IsArmed);
            Assert.Equal(1, io.LogCalls);
        }

        [Fact]
        public void Cancel_disarms_cts()
        {
            var io = new RecordingIoHost { StillStale = true };
            var owner = new HydrationDayRetryOwner(io);

            owner.ScheduleIfNeeded("stale");
            Assert.True(owner.IsArmed);

            owner.Cancel();
            Assert.False(owner.IsArmed);
        }

        [Fact]
        public void Schedule_skips_after_max_day_attempts()
        {
            var io = new RecordingIoHost { StillStale = true };
            var owner = new HydrationDayRetryOwner(io);
            DateTime now = DateTime.UtcNow;

            for (int i = 0; i < HistoryHydrationFallbackDecision.DefaultMaxDayAttempts; i++)
            {
                owner.NoteAttempt(now);
            }

            Assert.Equal(HistoryHydrationFallbackDecision.DefaultMaxDayAttempts, owner.DayAttempts);
            owner.ScheduleIfNeeded("capped");
            Assert.False(owner.IsArmed);
        }

        private sealed class RecordingIoHost : IHydrationDayRetryIoHost
        {
            public bool StillStale { get; set; }
            public int StampCalls { get; private set; }
            public int PipelineRuns { get; private set; }
            public int LogCalls { get; private set; }

            public bool TryGetFreshnessStaleReason(DateTime nowUtc, out string staleReason)
            {
                staleReason = StillStale ? "test-stale" : null;
                return StillStale;
            }

            public Task RunHydrationFallbackPipelineAsync(string reason)
            {
                PipelineRuns++;
                return Task.CompletedTask;
            }

            public void StampLastHydrationFallbackUtc(DateTime nowUtc) => StampCalls++;

            public void LogHydrationDayRetry(string message) => LogCalls++;
        }
    }
}
