// =============================================================================
// ConnectionSoftReconnectGates
//
// Schedule / loop / post-loop decisions for soft auto-reconnect.
// SoftReconnectCoordinator owns the reconnect lock / isReconnecting;
// ConnectAsync and pairing I/O stay on the host.
// =============================================================================

namespace Unison.WhatsAppClient.Helpers
{
    public enum SoftReconnectScheduleAction
    {
        /// <summary>Do not claim the reconnect slot.</summary>
        Skip,

        /// <summary>Claim the soft-reconnect slot and start the loop.</summary>
        StartLoop
    }

    public enum SoftReconnectPostLoopAction
    {
        None,

        /// <summary>Generic loop finished while still registered and disconnected.</summary>
        Reschedule,

        /// <summary>Pairing stage-2 was pending when the generic loop exited.</summary>
        PairingStage2Fallback
    }

    /// <summary>Pure soft-reconnect gates — no socket ownership.</summary>
    public static class ConnectionSoftReconnectGates
    {
        public static SoftReconnectScheduleAction DecideSchedule(
            bool suppressReconnect,
            bool fatalSessionEnded,
            bool isReconnecting,
            bool pairingRestartPending)
        {
            if (suppressReconnect ||
                fatalSessionEnded ||
                isReconnecting ||
                pairingRestartPending)
            {
                return SoftReconnectScheduleAction.Skip;
            }

            return SoftReconnectScheduleAction.StartLoop;
        }

        public static SoftReconnectPostLoopAction DecidePostLoop(
            bool suppressReconnect,
            bool fatalSessionEnded,
            bool pairingRestartPending,
            bool authPresent,
            bool authRegistered,
            bool isConnected)
        {
            bool restartNeeded =
                !suppressReconnect &&
                !fatalSessionEnded &&
                !pairingRestartPending &&
                authPresent &&
                authRegistered &&
                !isConnected;

            if (restartNeeded)
            {
                return SoftReconnectPostLoopAction.Reschedule;
            }

            if (pairingRestartPending && !isConnected)
            {
                return SoftReconnectPostLoopAction.PairingStage2Fallback;
            }

            return SoftReconnectPostLoopAction.None;
        }

        public static bool ShouldContinueLoop(bool suppressReconnect, bool fatalSessionEnded) =>
            !suppressReconnect && !fatalSessionEnded;

        public static bool ShouldExitForPairing(bool pairingRestartPending) =>
            pairingRestartPending;

        public static bool ShouldStopBecauseSessionNotRegistered(
            bool authPresent,
            bool authRegistered,
            bool fatalSessionEnded) =>
            !authPresent || !authRegistered || fatalSessionEnded;

        public static bool ShouldAbortAfterDelay(
            bool suppressReconnect,
            bool fatalSessionEnded,
            bool pairingRestartPending) =>
            suppressReconnect || fatalSessionEnded || pairingRestartPending;

        public static bool ShouldSkipPairingStage2Start(
            bool suppressReconnect,
            bool fatalSessionEnded) =>
            suppressReconnect || fatalSessionEnded;

        /// <summary>
        /// Pairing stage-2 host: abort when reconnect is suppressed or the session died.
        /// </summary>
        public static bool ShouldAbortPairingRestart(
            bool suppressReconnect,
            bool fatalSessionEnded) =>
            suppressReconnect || fatalSessionEnded;

        /// <summary>
        /// Pairing stage-2 host: may hand off to soft reconnect after stage-2 fails.
        /// </summary>
        public static bool CanScheduleSoftReconnect(
            bool suppressReconnect,
            bool fatalSessionEnded) =>
            !suppressReconnect && !fatalSessionEnded;

        /// <summary>
        /// After the loop exits, raise <c>OnError</c> only when a failure remains and
        /// reconnect was not deliberately stopped.
        /// </summary>
        public static bool ShouldRaiseReconnectLoopError(
            bool hasLastError,
            bool suppressReconnect,
            bool fatalSessionEnded) =>
            hasLastError && ShouldContinueLoop(suppressReconnect, fatalSessionEnded);
    }
}
