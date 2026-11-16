// =============================================================================
// MarkPairingRestartPendingOwner
//
// Owns IMarkPairingRestartPendingHost. The pairing-restart flag and the
// pre-session close streak are read by the reconnect path, which stays on the
// host, so they are reached through ports.
// =============================================================================
using System;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class MarkPairingRestartPendingOwner : IMarkPairingRestartPendingHost
    {
        private readonly Func<bool> _getPairingRestartPending;
        private readonly Action<bool> _setPairingRestartPending;
        private readonly Action _clearPreSessionCloseAsFatal;
        private readonly Action _resetPreSessionCloseStreak;
        private readonly Action<string> _trace;

        public MarkPairingRestartPendingOwner(
            Func<bool> getPairingRestartPending,
            Action<bool> setPairingRestartPending,
            Action clearPreSessionCloseAsFatal,
            Action resetPreSessionCloseStreak,
            Action<string> trace)
        {
            _getPairingRestartPending = getPairingRestartPending
                ?? throw new ArgumentNullException(nameof(getPairingRestartPending));
            _setPairingRestartPending = setPairingRestartPending
                ?? throw new ArgumentNullException(nameof(setPairingRestartPending));
            _clearPreSessionCloseAsFatal = clearPreSessionCloseAsFatal
                ?? throw new ArgumentNullException(nameof(clearPreSessionCloseAsFatal));
            _resetPreSessionCloseStreak = resetPreSessionCloseStreak
                ?? throw new ArgumentNullException(nameof(resetPreSessionCloseStreak));
            _trace = trace ?? throw new ArgumentNullException(nameof(trace));
        }

        public void Run(string reason) => MarkPairingRestartPendingDispatch.Run(this, reason);

        bool IMarkPairingRestartPendingHost.PairingRestartPending
        {
            get => _getPairingRestartPending();
            set => _setPairingRestartPending(value);
        }

        void IMarkPairingRestartPendingHost.ClearPreSessionCloseAsFatal() => _clearPreSessionCloseAsFatal();

        void IMarkPairingRestartPendingHost.ResetPreSessionCloseStreak() => _resetPreSessionCloseStreak();

        void IMarkPairingRestartPendingHost.TracePairingRestartPending(string reason) =>
            _trace("pairingRestartPending=true reason=" + (reason ?? string.Empty));
    }
}
