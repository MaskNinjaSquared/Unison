// =============================================================================
// SocketErrorDispatch
//
// OnStreamError / OnError bodies: stale gate, pairing 515, logout latch,
// transport failure reconnect. Handler wiring stays on the host.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;

namespace Unison.WhatsAppClient.Helpers
{
    public static class SocketErrorDispatch
    {
        public static void HandleStreamError(
            ISocketErrorHost host,
            string code,
            bool isCurrentSocket)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            bool explicitLogout = ExplicitLogoutStreamCode.Matches(code);
            if (StaleSocketEventGate.ShouldIgnore(isCurrentSocket, explicitLogout))
            {
                return;
            }

            if (PairingRestartStreamCode.Matches(code))
            {
                if (!isCurrentSocket)
                {
                    return;
                }

                host.MarkPairingRestartPending("stream-error-515");
                host.TryStartPairingStage2Reconnect("stream-error-515");
            }
            else if (explicitLogout)
            {
                host.LatchFatalSession("stream-" + (code ?? "logout"));
            }

            host.ReportStreamError(code);
        }

        public static async Task HandleErrorAsync(
            ISocketErrorHost host,
            Exception ex,
            bool isCurrentSocket,
            bool socketConnected,
            bool handshakeComplete)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (ex == null)
            {
                return;
            }

            string fatalCode = FatalStreamCodeFromException.TryRead(ex);
            bool explicitLogout = ExplicitLogoutStreamCode.Matches(fatalCode);

            if (StaleSocketEventGate.ShouldIgnore(isCurrentSocket, explicitLogout))
            {
                return;
            }

            host.LogSocketError(ex, socketConnected, handshakeComplete);

            if (explicitLogout)
            {
                host.LatchFatalSession("error-" + fatalCode);
            }

            if (fatalCode != null)
            {
                host.ReportStreamError(fatalCode);
            }

            if (host.SuppressReconnect || host.FatalSessionEnded)
            {
                host.RaiseOnError(ex);
                return;
            }

            if (!isCurrentSocket)
            {
                return;
            }

            bool transportFailure = SocketTransportFailure.Matches(
                ex,
                socketConnected,
                handshakeComplete);

            if (transportFailure)
            {
                if (host.PairingRestartPending)
                {
                    host.PairingTrace(
                        "transport failure during pairing stage2 → defer to ReconnectForPairingAsync");
                }
                else
                {
                    host.LogTransportFailureScheduled();
                    await host.DelayBeforeReconnectAsync(250).ConfigureAwait(false);
                    if (!host.FatalSessionEnded && !host.SuppressReconnect)
                    {
                        host.ScheduleAutoReconnect("socket-error");
                    }
                }
            }

            host.RaiseOnError(ex);
        }
    }
}
