// =============================================================================
// Characterization tests for WhatsAppMapper.ToUtc.
//
// This one function decides whether the chat list and the message strip agree
// about what "now" is. The Unspecified case is the interesting one: SQLite hands
// back UTC wall-clock with no kind, and calling ToUniversalTime on it would
// shift every persisted timestamp by the device's offset.
// =============================================================================
using System;
using Unison.Core.Mappers;
using Xunit;

namespace Unison.Core.Tests.Mappers
{
    public class WhatsAppMapperToUtcTests
    {
        [Fact]
        public void Utc_timestamps_pass_through_untouched()
        {
            var utc = new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);

            Assert.Equal(utc, WhatsAppMapper.ToUtc(utc));
            Assert.Equal(DateTimeKind.Utc, WhatsAppMapper.ToUtc(utc).Kind);
        }

        [Fact]
        public void Unspecified_is_relabelled_as_utc_without_shifting_the_clock()
        {
            // The regression this guards: on a UTC-3 device, ToUniversalTime would
            // turn 12:00 into 15:00 and push persisted rows out of order.
            var fromSqlite = new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Unspecified);

            var result = WhatsAppMapper.ToUtc(fromSqlite);

            Assert.Equal(DateTimeKind.Utc, result.Kind);
            Assert.Equal(fromSqlite.Ticks, result.Ticks);
        }

        [Fact]
        public void Local_timestamps_are_converted()
        {
            var local = new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Local);

            var result = WhatsAppMapper.ToUtc(local);

            Assert.Equal(DateTimeKind.Utc, result.Kind);
            Assert.Equal(local.ToUniversalTime(), result);
        }

        [Fact]
        public void MinValue_is_left_alone_as_the_no_timestamp_sentinel()
        {
            Assert.Equal(DateTime.MinValue, WhatsAppMapper.ToUtc(DateTime.MinValue));
        }
    }
}
