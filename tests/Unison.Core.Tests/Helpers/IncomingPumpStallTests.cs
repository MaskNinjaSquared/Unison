using System;
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class IncomingPumpStallTests
    {
        [Fact]
        public void A_running_pump_with_work_stalls_only_after_the_limit()
        {
            DateTime now = new DateTime(2026, 1, 1, 0, 0, 30, DateTimeKind.Utc);
            long stage = now.AddSeconds(-10).Ticks;
            var limit = TimeSpan.FromSeconds(10);

            Assert.False(IncomingPumpStall.IsStalled(false, stage, true, now, limit));
            Assert.False(IncomingPumpStall.IsStalled(true, 0, true, now, limit));
            Assert.False(IncomingPumpStall.IsStalled(true, stage, false, now, limit));
            Assert.False(IncomingPumpStall.IsStalled(true, stage, true, now, limit));
            Assert.True(IncomingPumpStall.IsStalled(true, now.AddSeconds(-10).AddTicks(-1).Ticks, true, now, limit));
        }
    }
}