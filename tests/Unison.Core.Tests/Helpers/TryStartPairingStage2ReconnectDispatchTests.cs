using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class TryStartPairingStage2ReconnectDispatchTests
    {
        [Fact]
        public void Suppress_skips_without_marking()
        {
            var host = new FakeHost { SuppressReconnect = true };
            TryStartPairingStage2ReconnectDispatch.Run(host, "515");
            Assert.False(host.Marked);
            Assert.False(host.Fired);
            Assert.Contains("skipped", host.LastTrace);
        }

        [Fact]
        public void In_flight_marks_but_does_not_fire()
        {
            var host = new FakeHost { Claim = false };
            TryStartPairingStage2ReconnectDispatch.Run(host, "515");
            Assert.True(host.Marked);
            Assert.False(host.Fired);
            Assert.Contains("already in-flight", host.LastTrace);
        }

        [Fact]
        public void Claim_starts_stage2()
        {
            var host = new FakeHost { Claim = true };
            TryStartPairingStage2ReconnectDispatch.Run(host, "pair");
            Assert.True(host.Marked);
            Assert.Equal("pairing-restart", host.HealthStopReason);
            Assert.True(host.Fired);
            Assert.Contains("START", host.LastTrace);
        }

        private sealed class FakeHost : ITryStartPairingStage2ReconnectHost
        {
            public bool SuppressReconnect { get; set; }
            public bool FatalSessionEnded { get; set; }
            public bool Claim = true;
            public bool Marked;
            public bool Fired;
            public string HealthStopReason;
            public string LastTrace;

            public void TracePairingStage2(string message) => LastTrace = message;

            public void MarkPairingRestartPending(string reason) => Marked = true;

            public bool TryClaimReconnectSlot() => Claim;

            public void StopConnectionHealthMonitor(string reason) =>
                HealthStopReason = reason;

            public void FireAndForgetPairingStage2Reconnect() => Fired = true;
        }
    }
}
