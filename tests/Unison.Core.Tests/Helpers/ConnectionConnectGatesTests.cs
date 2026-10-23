using Unison.Core.Contracts.WhatsApp;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ConnectionConnectGatesTests
    {
        [Fact]
        public void Skips_when_healthy_and_registered()
        {
            var source = new FakeSource { HealthyRegistered = true };
            Assert.Equal(ConnectionConnectGate.SkipAlreadyHealthy, ConnectionConnectGates.Decide(source));
        }

        [Fact]
        public void Aborts_when_session_died()
        {
            var source = new FakeSource { Refuse = true };
            Assert.Equal(ConnectionConnectGate.AbortFatalSession, ConnectionConnectGates.Decide(source));
        }

        [Fact]
        public void Proceeds_for_qr_and_journals_force_fresh()
        {
            var source = new FakeSource { HealthyUnregistered = true };
            Assert.Equal(ConnectionConnectGate.Proceed, ConnectionConnectGates.Decide(source));
            Assert.True(ConnectionConnectGates.ShouldJournalForceFreshForQr(source));
        }

        [Fact]
        public void Unregistered_fatal_still_proceeds_for_qr()
        {
            // Mirrors ShouldRefuseConnectBecauseSessionDied: fatal + unregistered → allow.
            var source = new FakeSource { HealthyUnregistered = true, Refuse = false };
            Assert.Equal(ConnectionConnectGate.Proceed, ConnectionConnectGates.Decide(source));
        }

        private sealed class FakeSource : IConnectionConnectGateSource
        {
            public bool HealthyRegistered { get; set; }
            public bool HealthyUnregistered { get; set; }
            public bool Refuse { get; set; }

            public bool HasHealthyRegisteredSocket => HealthyRegistered;
            public bool HasHealthyUnregisteredSocket => HealthyUnregistered;
            public bool ShouldRefuseConnectBecauseSessionDied => Refuse;
        }
    }
}
