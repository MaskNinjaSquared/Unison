using System;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class NoteHydrationDayAttemptDispatchTests
    {
        [Fact]
        public void First_attempt_resets_day_and_increments()
        {
            var host = new FakeHost { Anchor = DateTime.MinValue };
            var now = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
            NoteHydrationDayAttemptDispatch.Run(host, now);
            Assert.Equal(now.Date, host.Anchor);
            Assert.Equal(1, host.Attempts);
            Assert.Equal(now, host.LastFallback);
        }

        [Fact]
        public void Same_day_does_not_reset()
        {
            var day = new DateTime(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc);
            var host = new FakeHost { Anchor = day, Attempts = 2 };
            var now = day.AddHours(5);
            NoteHydrationDayAttemptDispatch.Run(host, now);
            Assert.Equal(3, host.Attempts);
            Assert.False(host.ResetCalled);
        }

        private sealed class FakeHost : INoteHydrationDayAttemptHost
        {
            public DateTime Anchor;
            public int Attempts;
            public DateTime LastFallback;
            public bool ResetCalled;

            public DateTime HydrationDayAnchorUtc => Anchor;

            public void ResetHydrationDay(DateTime dayUtc)
            {
                ResetCalled = true;
                Anchor = dayUtc;
                Attempts = 0;
            }

            public void IncrementHydrationDayAttempts() => Attempts++;

            public void StampLastHydrationFallbackUtc(DateTime nowUtc) =>
                LastFallback = nowUtc;
        }
    }
}
