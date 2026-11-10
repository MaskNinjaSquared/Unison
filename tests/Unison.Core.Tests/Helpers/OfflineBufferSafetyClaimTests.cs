using Unison.Socket.Session;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class OfflineBufferSafetyClaimTests
    {
        [Fact]
        public void Timeout_before_connected_defers_without_claim()
        {
            var claim = new OfflineBufferSafetyClaim();

            Assert.Equal(
                OfflineBufferSafetyOutcome.Deferred,
                claim.OnSafetyTimeout(isConnected: false));
            Assert.False(claim.ReceivedPendingNotifications);
            Assert.True(claim.HasDeferredClaim);
        }

        [Fact]
        public void Deferred_claim_fires_once_when_connected()
        {
            var claim = new OfflineBufferSafetyClaim();
            claim.OnSafetyTimeout(isConnected: false);

            Assert.Equal(
                OfflineBufferSafetyOutcome.ClaimPendingNotifications,
                claim.OnConnected());
            Assert.True(claim.ReceivedPendingNotifications);
            Assert.False(claim.HasDeferredClaim);

            Assert.Equal(
                OfflineBufferSafetyOutcome.SkippedNotPending,
                claim.OnConnected());
        }

        [Fact]
        public void Timeout_while_connected_claims_immediately()
        {
            var claim = new OfflineBufferSafetyClaim();

            Assert.Equal(
                OfflineBufferSafetyOutcome.ClaimPendingNotifications,
                claim.OnSafetyTimeout(isConnected: true));
            Assert.True(claim.ReceivedPendingNotifications);
        }

        [Fact]
        public void Server_receipt_clears_deferred_and_blocks_claim()
        {
            var claim = new OfflineBufferSafetyClaim();
            claim.OnSafetyTimeout(isConnected: false);
            claim.MarkReceivedFromServer();

            Assert.True(claim.ReceivedPendingNotifications);
            Assert.False(claim.HasDeferredClaim);
            // Deferred flag was cleared with the server receipt, so connect is a no-op.
            Assert.Equal(
                OfflineBufferSafetyOutcome.SkippedNotPending,
                claim.OnConnected());
        }

        [Fact]
        public void Timeout_after_server_receipt_is_skipped()
        {
            var claim = new OfflineBufferSafetyClaim();
            claim.MarkReceivedFromServer();

            Assert.Equal(
                OfflineBufferSafetyOutcome.SkippedAlreadyReceived,
                claim.OnSafetyTimeout(isConnected: true));
        }
    }
}
