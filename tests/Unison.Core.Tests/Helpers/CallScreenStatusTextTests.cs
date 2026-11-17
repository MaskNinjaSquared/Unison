using System;
using Unison.Core.Helpers.Calls;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class CallScreenStatusTextTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 16, 20, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Dialing_and_ringing_use_their_labels()
        {
            Assert.Equal("DIALING...", CallScreenStatusText.For(ActiveCallPhase.Dialing, null, Now, "DIALING...", "RECEIVING CALL...", "CALL ENDED"));
            Assert.Equal("RECEIVING CALL...", CallScreenStatusText.For(ActiveCallPhase.Ringing, null, Now, "DIALING...", "RECEIVING CALL...", "CALL ENDED"));
            Assert.Equal("CONNECTING...", CallScreenStatusText.For(ActiveCallPhase.Connecting, null, Now, "DIALING...", "RECEIVING CALL...", "CALL ENDED", "CONNECTING..."));
            Assert.Equal("CALL ENDED", CallScreenStatusText.For(ActiveCallPhase.Ended, null, Now, "DIALING...", "RECEIVING CALL...", "CALL ENDED"));
        }

        [Fact]
        public void Active_shows_running_minutes_and_seconds()
        {
            Assert.Equal("00:00", CallScreenStatusText.For(ActiveCallPhase.Active, Now, Now, "d", "r", "e"));
            Assert.Equal("01:20", CallScreenStatusText.For(ActiveCallPhase.Active, Now.AddSeconds(-80), Now, "d", "r", "e"));
        }

        [Fact]
        public void Active_past_an_hour_adds_hours()
        {
            Assert.Equal("1:02:03", CallScreenStatusText.For(ActiveCallPhase.Active, Now.AddSeconds(-3723), Now, "d", "r", "e"));
        }

        [Fact]
        public void Active_without_connect_time_or_with_clock_skew_shows_zero()
        {
            Assert.Equal("00:00", CallScreenStatusText.For(ActiveCallPhase.Active, null, Now, "d", "r", "e"));
            Assert.Equal("00:00", CallScreenStatusText.For(ActiveCallPhase.Active, Now.AddSeconds(5), Now, "d", "r", "e"));
        }
    }
}
