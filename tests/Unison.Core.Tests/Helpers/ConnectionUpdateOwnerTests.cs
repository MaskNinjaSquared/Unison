using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ConnectionUpdateOwnerTests
    {
        [Fact]
        public void Shutdown_still_publishes_status()
        {
            string published = null;
            bool ignored = false;
            var owner = new ConnectionUpdateOwner(
                suppressReconnect: () => true,
                fatalSessionEnded: () => false,
                isAuthRegistered: () => true,
                pairingRestartPending: () => false,
                sessionEstablishedThisConnection: () => true,
                countPreSessionCloseAsFatal: () => false,
                qrDeliveredThisConnection: () => false,
                preSessionCloseFatalThreshold: () => 3,
                logIgnoredDuringShutdown: _ => ignored = true,
                publishConnectionUpdate: status => published = status,
                writePairingRestartDiagnostics: () => { },
                pairingTrace: _ => { },
                stopConnectionHealthMonitor: _ => { },
                writePairingRestartCloseIgnored: () => { },
                writePairingCloseBeforeRestart: () => { },
                tryStartPairingStage2: _ => { },
                incrementPreSessionCloseStreak: () => 1,
                writePreSessionClose: _ => { },
                raiseInvalidSessionSuspected: _ => { },
                resetPreSessionCloseStreak: () => { },
                scheduleAutoReconnect: _ => { },
                writeQrExpired: _ => { },
                raiseQrExpired: () => { });

            owner.Handle("close");

            Assert.True(ignored);
            Assert.Equal("close", published);
        }
    }
}
