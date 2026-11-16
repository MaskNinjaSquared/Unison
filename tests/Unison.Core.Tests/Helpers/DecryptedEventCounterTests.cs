using System;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class DecryptedEventCounterTests
    {
        [Fact]
        public void Observe_counts_and_stamps_the_last_event()
        {
            var counter = new DecryptedEventCounter();
            var at = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

            counter.Observe(at.AddSeconds(-5));
            counter.Observe(at);

            Assert.Equal(2, counter.Count);
            Assert.Equal(at.Ticks, counter.LastUtcTicks);
        }

        [Fact]
        public void A_new_counter_reports_nothing_observed()
        {
            var counter = new DecryptedEventCounter();

            Assert.Equal(0, counter.Count);
            Assert.Equal(0, counter.LastUtcTicks);
        }
    }
}
