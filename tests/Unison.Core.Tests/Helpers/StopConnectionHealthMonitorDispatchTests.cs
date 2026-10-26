using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class StopConnectionHealthMonitorDispatchTests
    {
        [Fact]
        public void No_cts_still_stops_periodic()
        {
            var host = new FakeHost { Cancelled = false };
            StopConnectionHealthMonitorDispatch.Run(host, "gone");
            Assert.False(host.Logged);
            Assert.Equal("gone", host.PeriodicReason);
        }

        [Fact]
        public void Cancelled_cts_logs_then_stops_periodic()
        {
            var host = new FakeHost { Cancelled = true };
            StopConnectionHealthMonitorDispatch.Run(host, "logout");
            Assert.True(host.Logged);
            Assert.Equal("logout", host.PeriodicReason);
        }

        private sealed class FakeHost : IStopConnectionHealthMonitorHost
        {
            public bool Cancelled;
            public bool Logged;
            public string PeriodicReason;

            public bool TryCancelConnectionHealthMonitor() => Cancelled;

            public void LogConnectionHealthMonitorStopped(string reason) => Logged = true;

            public void StopPeriodicFullHistorySafety(string reason) =>
                PeriodicReason = reason;
        }
    }
}
