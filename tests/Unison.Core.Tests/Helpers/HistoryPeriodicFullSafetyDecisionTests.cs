using Unison.Core.Helpers;
using Xunit;
using Unison.WhatsAppClient.Helpers;

namespace Unison.Core.Tests.Helpers
{
    public sealed class HistoryPeriodicFullSafetyDecisionTests
    {
        [Fact]
        public void Interval_is_five_minutes()
        {
            Assert.Equal(System.TimeSpan.FromMinutes(5), HistoryPeriodicFullSafetyDecision.Interval);
        }

        [Theory]
        [InlineData(false, false, false, true, true, true, false, "socket-not-ready")]
        [InlineData(true, true, false, true, true, true, false, "catch-up-active")]
        [InlineData(true, false, true, true, true, true, false, "full-claimed")]
        [InlineData(true, false, false, false, false, true, false, "no-debt")]
        [InlineData(true, false, false, true, false, true, true, "force-repair")]
        [InlineData(true, false, false, false, true, true, true, "tip-stale")]
        [InlineData(true, false, false, true, true, true, true, "repair-and-stale")]
        [InlineData(true, false, false, true, true, false, false, "cascade-disabled")]
        [InlineData(true, false, false, false, true, false, false, "cascade-disabled")]
        public void ShouldRequest_gates_on_debt_and_idle(
            bool socketReady,
            bool catchUpActive,
            bool fullClaimed,
            bool forceRepair,
            bool tipStale,
            bool allowCascade,
            bool expected,
            string expectedReason)
        {
            bool ok = HistoryPeriodicFullSafetyDecision.ShouldRequest(
                socketReady,
                catchUpActive,
                fullClaimed,
                forceRepair,
                tipStale,
                allowCascade,
                out string reason);

            Assert.Equal(expected, ok);
            Assert.Equal(expectedReason, reason);
        }
    }
}
