using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class StopPeriodicFullHistorySafetyDispatchTests
    {
        [Fact]
        public void No_cts_is_noop()
        {
            var host = new FakeHost { Cancelled = false };
            StopPeriodicFullHistorySafetyDispatch.Run(host, "x");
            Assert.Null(host.LoggedReason);
        }

        [Fact]
        public void Cancelled_cts_logs_stop()
        {
            var host = new FakeHost { Cancelled = true };
            StopPeriodicFullHistorySafetyDispatch.Run(host, "logout");
            Assert.Equal("logout", host.LoggedReason);
        }

        private sealed class FakeHost : IStopPeriodicFullHistorySafetyHost
        {
            public bool Cancelled;
            public string LoggedReason;

            public bool TryCancelPeriodicFullHistorySafety() => Cancelled;

            public void LogPeriodicFullHistorySafetyStopped(string reason) =>
                LoggedReason = reason;
        }
    }
}
