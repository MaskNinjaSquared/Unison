using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ConnectionCloseDispositionTests
    {
        [Fact]
        public void Restart_claims_stage2()
        {
            Assert.Equal(
                ConnectionCloseAction.ClaimPairingStage2,
                ConnectionCloseDisposition.ForRestartStatus());
        }

        [Fact]
        public void Registered_close_skips_when_pairing_owns()
        {
            Assert.Equal(
                ConnectionCloseAction.SkipPairingOwnsReconnect,
                ConnectionCloseDisposition.ForRegisteredClose(
                    pairingRestartPending: true,
                    sessionEstablishedThisConnection: false,
                    countPreSessionCloseAsFatal: true));
        }

        [Fact]
        public void Registered_close_before_restart_claims_stage2()
        {
            Assert.Equal(
                ConnectionCloseAction.ClaimPairingStage2,
                ConnectionCloseDisposition.ForRegisteredClose(
                    pairingRestartPending: false,
                    sessionEstablishedThisConnection: false,
                    countPreSessionCloseAsFatal: false));
        }

        [Fact]
        public void Registered_pre_session_counts_streak()
        {
            Assert.Equal(
                ConnectionCloseAction.CountPreSessionStreak,
                ConnectionCloseDisposition.ForRegisteredClose(
                    pairingRestartPending: false,
                    sessionEstablishedThisConnection: false,
                    countPreSessionCloseAsFatal: true));
        }

        [Fact]
        public void Unregistered_qr_close_expires()
        {
            Assert.Equal(
                ConnectionCloseAction.QrExpired,
                ConnectionCloseDisposition.ForUnregisteredClose(
                    qrDeliveredThisConnection: true,
                    pairingRestartPending: false,
                    authRegistered: false));
        }

        [Fact]
        public void Pre_session_streak_threshold()
        {
            Assert.False(ConnectionCloseDisposition.PreSessionStreakReachedFatal(2, 3));
            Assert.True(ConnectionCloseDisposition.PreSessionStreakReachedFatal(3, 3));
        }
    }
}
