using System;
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ElapsedTimeFormatterTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 3, 17, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Under_a_minute_is_just_now()
        {
            Assert.Equal("Just now", ElapsedTimeFormatter.Format(Now.AddSeconds(-30), Now, null));
        }

        [Fact]
        public void One_minute_is_singular()
        {
            Assert.Equal("1 minute ago", ElapsedTimeFormatter.Format(Now.AddSeconds(-90), Now, null));
        }

        [Fact]
        public void Minutes_are_plural()
        {
            Assert.Equal("12 minutes ago", ElapsedTimeFormatter.Format(Now.AddMinutes(-12), Now, null));
        }

        [Fact]
        public void One_hour_is_singular()
        {
            Assert.Equal("1 hour ago", ElapsedTimeFormatter.Format(Now.AddMinutes(-75), Now, null));
        }

        [Fact]
        public void Hours_are_plural()
        {
            Assert.Equal("5 hours ago", ElapsedTimeFormatter.Format(Now.AddHours(-5), Now, null));
        }

        [Fact]
        public void Missing_timestamp_is_empty()
        {
            Assert.Equal(string.Empty, ElapsedTimeFormatter.Format(null, Now, null));
        }

        [Fact]
        public void Future_timestamp_reads_as_just_now()
        {
            Assert.Equal("Just now", ElapsedTimeFormatter.Format(Now.AddMinutes(3), Now, null));
        }
    }
}
