using System;
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class UnixTimeToMillisecondsTests
    {
        [Fact]
        public void A_utc_instant_turns_into_its_milliseconds()
        {
            var utc = new DateTime(2026, 3, 10, 12, 0, 0, 250, DateTimeKind.Utc);

            Assert.Equal(new DateTimeOffset(utc).ToUnixTimeMilliseconds(), UnixTime.ToMilliseconds(utc));
        }

        [Fact]
        public void An_unspecified_time_is_read_as_local()
        {
            var unspecified = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Unspecified);
            var local = DateTime.SpecifyKind(unspecified, DateTimeKind.Local);

            Assert.Equal(new DateTimeOffset(local).ToUnixTimeMilliseconds(), UnixTime.ToMilliseconds(unspecified));
        }

        [Fact]
        public void Milliseconds_round_trip_through_utc()
        {
            var utc = new DateTime(2026, 3, 10, 12, 0, 0, 250, DateTimeKind.Utc);

            Assert.Equal(utc, UnixTime.FromMilliseconds(UnixTime.ToMilliseconds(utc)));
        }
    }
}
