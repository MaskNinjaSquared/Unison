using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class HistorySyncFlowDecisionTests
    {
        [Theory]
        [InlineData(true, false, false, true)]
        [InlineData(false, true, false, true)]
        [InlineData(false, false, true, true)]
        [InlineData(false, false, false, false)]
        public void Pairing_bootstrap_active_when_any_gate_is_up(
            bool awaiting,
            bool applyInFlight,
            bool chunkSeen,
            bool expected)
        {
            Assert.Equal(
                expected,
                HistorySyncFlowDecision.IsPairingBootstrapActive(
                    awaiting,
                    applyInFlight,
                    chunkSeen));
        }

        [Fact]
        public void Post_init_full_history_blocked_during_pairing_bootstrap()
        {
            Assert.False(HistorySyncFlowDecision.ShouldSolicitPostInitFullHistory(true));
            Assert.True(HistorySyncFlowDecision.ShouldSolicitPostInitFullHistory(false));
        }

        [Fact]
        public void Adjacent_catch_up_blocked_during_pairing_bootstrap()
        {
            Assert.False(HistorySyncFlowDecision.ShouldRequestAdjacentCatchUp(true));
            Assert.True(HistorySyncFlowDecision.ShouldRequestAdjacentCatchUp(false));
        }
    }
}
