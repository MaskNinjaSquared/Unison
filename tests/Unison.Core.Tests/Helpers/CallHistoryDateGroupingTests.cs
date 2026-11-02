using System;
using System.Collections.Generic;
using Unison.Core.Helpers.Calls;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class CallHistoryDateGroupingTests
    {
        [Fact]
        public void Build_buckets_by_local_day_newest_first()
        {
            DateTime todayLocal = DateTime.Today;
            DateTime yesterdayLocal = todayLocal.AddDays(-1);

            var entries = new[]
            {
                new CallHistoryEntry
                {
                    CallId = "old",
                    TimestampUtc = yesterdayLocal.ToUniversalTime().AddHours(8)
                },
                new CallHistoryEntry
                {
                    CallId = "new",
                    TimestampUtc = todayLocal.ToUniversalTime().AddHours(10)
                },
                new CallHistoryEntry
                {
                    CallId = "new2",
                    TimestampUtc = todayLocal.ToUniversalTime().AddHours(12)
                }
            };

            IReadOnlyList<CallHistoryDateGroup> groups =
                CallHistoryDateGrouping.Build(entries, "Today", "Yesterday");

            Assert.Equal(2, groups.Count);
            Assert.Equal("Today", groups[0].Key);
            Assert.Equal(2, groups[0].Count);
            Assert.Equal("new2", groups[0][0].CallId);
            Assert.Equal("Yesterday", groups[1].Key);
            Assert.Equal("old", groups[1][0].CallId);
        }
    }
}
