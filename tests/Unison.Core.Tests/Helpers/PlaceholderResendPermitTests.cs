using System;
using System.Threading;
using Unison.Core.Helpers;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class PlaceholderResendPermitTests
    {
        [Fact]
        public void A_quiet_candidate_may_be_asked()
        {
            var candidate = new MissingMessageCandidate();
            Assert.True(PlaceholderResendPermit.MaySchedule(candidate, DateTime.UtcNow, TimeSpan.FromSeconds(15)));
        }

        [Fact]
        public void A_second_try_an_in_flight_ask_or_a_fresh_answer_waits()
        {
            DateTime now = new DateTime(2026, 1, 1, 0, 0, 30, DateTimeKind.Utc);
            var twice = new MissingMessageCandidate { PlaceholderRequestCount = 2 };
            var flying = new MissingMessageCandidate { PlaceholderRequestInFlight = true };
            var scheduled = new MissingMessageCandidate { PlaceholderScheduleCts = new CancellationTokenSource() };
            var recent = new MissingMessageCandidate { LastPlaceholderRequestUtc = now.AddSeconds(-14) };

            Assert.False(PlaceholderResendPermit.MaySchedule(twice, now, TimeSpan.FromSeconds(15)));
            Assert.False(PlaceholderResendPermit.MaySchedule(flying, now, TimeSpan.FromSeconds(15)));
            Assert.False(PlaceholderResendPermit.MaySchedule(scheduled, now, TimeSpan.FromSeconds(15)));
            Assert.False(PlaceholderResendPermit.MaySchedule(recent, now, TimeSpan.FromSeconds(15)));
            Assert.True(PlaceholderResendPermit.MaySchedule(
                new MissingMessageCandidate { LastPlaceholderRequestUtc = now.AddSeconds(-15) },
                now,
                TimeSpan.FromSeconds(15)));
        }
    }
}
