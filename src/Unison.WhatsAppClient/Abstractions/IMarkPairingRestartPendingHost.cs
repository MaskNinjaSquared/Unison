// =============================================================================
// IMarkPairingRestartPendingHost
//
// Side effects for MarkPairingRestartPendingDispatch.
// =============================================================================

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IMarkPairingRestartPendingHost
    {
        bool PairingRestartPending { get; set; }

        void ClearPreSessionCloseAsFatal();

        void ResetPreSessionCloseStreak();

        void TracePairingRestartPending(string reason);
    }
}
