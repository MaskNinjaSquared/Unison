// =============================================================================
// ConnectionUpdateDispatch
//
// OnConnectionUpdate body: shutdown ignore, route classify, close disposition
// apply, publish. Handler wiring stays on the host.
// =============================================================================
using System;
using Unison.Core.Contracts.WhatsApp;

namespace Unison.WhatsAppClient.Helpers
{
    public static class ConnectionUpdateDispatch
    {
        /// <summary>
        /// Runs the connection-update policy and always publishes the status
        /// (including early publish when a close action asks to stop falling through).
        /// </summary>
        public static void Handle(IConnectionUpdateHost host, string status)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (host.SuppressReconnect || host.FatalSessionEnded)
            {
                host.LogIgnoredDuringShutdown(status);
                host.PublishConnectionUpdate(status);
                return;
            }

            bool registered = host.IsAuthRegistered;
            switch (ConnectionUpdateRoute.Classify(status, registered))
            {
                case ConnectionUpdateRouteKind.Restart:
                    host.WritePairingRestartDiagnostics();
                    host.PairingTrace("connection status=restart → stage2");
                    ApplyCloseAction(
                        host,
                        ConnectionCloseDisposition.ForRestartStatus(),
                        "connection-update-restart");
                    break;

                case ConnectionUpdateRouteKind.RegisteredClose:
                    host.StopConnectionHealthMonitor("socket-close");
                    ConnectionCloseAction closeAction = ConnectionCloseDisposition.ForRegisteredClose(
                        host.PairingRestartPending,
                        host.SessionEstablishedThisConnection,
                        host.CountPreSessionCloseAsFatal);
                    if (ApplyCloseAction(host, closeAction, "socket-close"))
                    {
                        host.PublishConnectionUpdate(status);
                        return;
                    }

                    break;

                case ConnectionUpdateRouteKind.UnregisteredClose:
                    ConnectionCloseAction qrAction = ConnectionCloseDisposition.ForUnregisteredClose(
                        host.QrDeliveredThisConnection,
                        host.PairingRestartPending,
                        registered);
                    ApplyCloseAction(host, qrAction, "socket-close");
                    break;
            }

            host.PublishConnectionUpdate(status);
        }

        /// <summary>
        /// Applies <see cref="ConnectionCloseDisposition"/> side effects. True when the
        /// update handler should publish and return without falling through.
        /// </summary>
        public static bool ApplyCloseAction(
            IConnectionUpdateHost host,
            ConnectionCloseAction action,
            string trigger)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            switch (action)
            {
                case ConnectionCloseAction.SkipPairingOwnsReconnect:
                    host.WritePairingRestartCloseIgnored();
                    host.PairingTrace(
                        "close while pairingRestartPending → SKIP ScheduleAutoReconnect " +
                        "(stage2 owner)");
                    return true;

                case ConnectionCloseAction.ClaimPairingStage2:
                    if (string.Equals(trigger, "connection-update-restart", StringComparison.Ordinal))
                    {
                        host.TryStartPairingStage2Reconnect(trigger);
                        return false;
                    }

                    host.WritePairingCloseBeforeRestart();
                    host.PairingTrace(
                        "close BEFORE restart flag → claim stage2 (close-before-restart race)");
                    host.TryStartPairingStage2Reconnect("close-before-restart");
                    return true;

                case ConnectionCloseAction.CountPreSessionStreak:
                {
                    int streak = host.IncrementPreSessionCloseStreak();
                    host.WritePreSessionClose(streak);
                    if (ConnectionCloseDisposition.PreSessionStreakReachedFatal(
                            streak,
                            host.PreSessionCloseFatalThreshold))
                    {
                        host.RaiseInvalidSessionSuspected("pre-session-close-streak");
                        if (host.FatalSessionEnded)
                        {
                            return true;
                        }
                    }

                    host.ScheduleAutoReconnect(trigger);
                    return false;
                }

                case ConnectionCloseAction.SoftReconnectAfterEstablished:
                    host.ResetPreSessionCloseStreak();
                    host.ScheduleAutoReconnect(trigger);
                    return false;

                case ConnectionCloseAction.SoftReconnect:
                    host.ScheduleAutoReconnect(trigger);
                    return false;

                case ConnectionCloseAction.QrExpired:
                    host.WriteQrExpired(trigger);
                    host.PairingTrace("socket closed while unregistered; QR expired");
                    host.RaiseQrExpired();
                    return false;

                default:
                    return false;
            }
        }
    }
}
