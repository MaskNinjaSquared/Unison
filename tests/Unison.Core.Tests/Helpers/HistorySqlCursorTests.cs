using System;
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class HistorySqlCursorTests
    {
        [Fact]
        public void Utc_stamp_becomes_unspecified_with_same_wall_clock()
        {
            DateTime utc = new DateTime(2026, 3, 10, 15, 30, 0, DateTimeKind.Utc);

            DateTime cursor = HistorySqlCursor.ForQuery(utc);

            Assert.Equal(DateTimeKind.Unspecified, cursor.Kind);
            Assert.Equal(utc.Ticks, cursor.Ticks);
        }

        [Fact]
        public void Unspecified_sqlite_stamp_stays_wall_clock_utc()
        {
            DateTime stored = new DateTime(2026, 3, 10, 15, 30, 0, DateTimeKind.Unspecified);

            DateTime cursor = HistorySqlCursor.ForQuery(stored);

            Assert.Equal(DateTimeKind.Unspecified, cursor.Kind);
            Assert.Equal(stored.Ticks, cursor.Ticks);
        }

        [Fact]
        public void Local_stamp_converts_to_utc_wall_clock_then_unspecified()
        {
            DateTime local = new DateTime(2026, 3, 10, 12, 30, 0, DateTimeKind.Local);
            DateTime expectedUtc = local.ToUniversalTime();

            DateTime cursor = HistorySqlCursor.ForQuery(local);

            Assert.Equal(DateTimeKind.Unspecified, cursor.Kind);
            Assert.Equal(expectedUtc.Ticks, cursor.Ticks);
        }
    }
}
