// =============================================================================
// ConnectionCloseDisposition
//
// What a socket "close" status means for reconnect: stage-2 claim, pre-session
// streak, soft reconnect, or QR expired. Side effects stay on the host.
// =============================================================================
using System;

namespace Unison.WhatsAppClient.Helpers
{
    public enum ConnectionCloseAction
    {
        /// <summary>Ignore reconnect; stage-2 already owns the window.</summary>
        SkipPairingOwnsReconnect,

        /// <summary>Registered close before restart — claim pairing stage-2.</summary>
        ClaimPairingStage2,

        /// <summary>Count toward pre-session-close fatal streak (may raise bad-session).</summary>
        CountPreSessionStreak,

        /// <summary>Session was established; reset streak and soft-reconnect.</summary>
        SoftReconnectAfterEstablished,

        /// <summary>Soft-reconnect without streak work.</summary>
        SoftReconnect,

        /// <summary>Unregistered QR close — surface expired QR.</summary>
        QrExpired,

        /// <summary>No special reconnect policy for this close.</summary>
        None
    }

    /// <summary>Pure disposition for a connection-update <c>close</c> / <c>restart</c>.</summary>
    public static class ConnectionCloseDisposition
    {
        public static ConnectionCloseAction ForRestartStatus() =>
            ConnectionCloseAction.ClaimPairingStage2;

        public static ConnectionCloseAction ForRegisteredClose(
            bool pairingRestartPending,
            bool sessionEstablishedThisConnection,
            bool countPreSessionCloseAsFatal)
        {
            if (pairingRestartPending)
            {
                return ConnectionCloseAction.SkipPairingOwnsReconnect;
            }

            if (!sessionEstablishedThisConnection && !countPreSessionCloseAsFatal)
            {
                return ConnectionCloseAction.ClaimPairingStage2;
            }

            if (!sessionEstablishedThisConnection && countPreSessionCloseAsFatal)
            {
                return ConnectionCloseAction.CountPreSessionStreak;
            }

            if (sessionEstablishedThisConnection)
            {
                return ConnectionCloseAction.SoftReconnectAfterEstablished;
            }

            return ConnectionCloseAction.SoftReconnect;
        }

        public static ConnectionCloseAction ForUnregisteredClose(
            bool qrDeliveredThisConnection,
            bool pairingRestartPending,
            bool authRegistered)
        {
            if (authRegistered)
            {
                return ConnectionCloseAction.None;
            }

            if (qrDeliveredThisConnection && !pairingRestartPending)
            {
                return ConnectionCloseAction.QrExpired;
            }

            return ConnectionCloseAction.None;
        }

        /// <summary>
        /// After counting a pre-session close, whether the streak crossed the fatal threshold.
        /// </summary>
        public static bool PreSessionStreakReachedFatal(int streak, int threshold)
        {
            if (threshold < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(threshold));
            }

            return streak >= threshold;
        }
    }
}
