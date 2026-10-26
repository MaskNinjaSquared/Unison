using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class StartPeriodicFullHistorySafetyDispatchTests
    {
        [Fact]
        public void Stops_restarts_and_logs()
        {
            var host = new FakeHost { IntervalMinutes = 30 };
            StartPeriodicFullHistorySafetyDispatch.Run(host);
            Assert.Equal("restart", host.StopReason);
            Assert.True(host.LoopStarted);
            Assert.Equal(30, host.LoggedInterval);
        }

        private sealed class FakeHost : IStartPeriodicFullHistorySafetyHost
        {
            public string StopReason;
            public bool LoopStarted;
            public double IntervalMinutes;
            public double LoggedInterval;

            public void StopPeriodicFullHistorySafety(string reason) =>
                StopReason = reason;

            public void StartPeriodicFullHistorySafetyLoop() => LoopStarted = true;

            public double PeriodicFullHistorySafetyIntervalMinutes => IntervalMinutes;

            public void LogPeriodicFullHistorySafetyStarted(double intervalMinutes) =>
                LoggedInterval = intervalMinutes;
        }
    }
}
