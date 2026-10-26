using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class SocketErrorOwnerTests
    {
        [Fact]
        public void Stream_error_515_starts_pairing_stage2()
        {
            string marked = null;
            string stage2 = null;
            var owner = new SocketErrorOwner(
                suppressReconnect: () => false,
                fatalSessionEnded: () => false,
                pairingRestartPending: () => false,
                markPairingRestartPending: reason => marked = reason,
                tryStartPairingStage2: trigger => stage2 = trigger,
                latchFatalSession: _ => { },
                reportStreamError: _ => { },
                logSocketError: (ex, c, h) => { },
                pairingTrace: _ => { },
                logTransportFailureScheduled: () => { },
                delayBeforeReconnectAsync: _ => Task.CompletedTask,
                scheduleAutoReconnect: _ => { },
                raiseOnError: _ => { });

            owner.HandleStreamError("515", isCurrentSocket: true);

            Assert.Equal("stream-error-515", marked);
            Assert.Equal("stream-error-515", stage2);
        }

        [Fact]
        public async Task Transport_failure_schedules_reconnect()
        {
            string reconnect = null;
            bool raised = false;
            var owner = new SocketErrorOwner(
                suppressReconnect: () => false,
                fatalSessionEnded: () => false,
                pairingRestartPending: () => false,
                markPairingRestartPending: _ => { },
                tryStartPairingStage2: _ => { },
                latchFatalSession: _ => { },
                reportStreamError: _ => { },
                logSocketError: (ex, c, h) => { },
                pairingTrace: _ => { },
                logTransportFailureScheduled: () => { },
                delayBeforeReconnectAsync: _ => Task.CompletedTask,
                scheduleAutoReconnect: trigger => reconnect = trigger,
                raiseOnError: _ => raised = true);

            await owner.HandleErrorAsync(
                new Exception("connection was aborted"),
                isCurrentSocket: true,
                socketConnected: false,
                handshakeComplete: false);

            Assert.Equal("socket-error", reconnect);
            Assert.True(raised);
        }
    }
}
