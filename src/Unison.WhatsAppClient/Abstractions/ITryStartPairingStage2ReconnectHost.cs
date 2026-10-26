// =============================================================================
// ITryStartPairingStage2ReconnectHost
//
// Side effects for TryStartPairingStage2ReconnectDispatch.
// =============================================================================

namespace Unison.WhatsAppClient.Abstractions
{
    public interface ITryStartPairingStage2ReconnectHost
    {
        bool SuppressReconnect { get; }

        bool FatalSessionEnded { get; }

        void TracePairingStage2(string message);

        void MarkPairingRestartPending(string reason);

        /// <summary>Under reconnect lock: claim the reconnect slot. False if already in flight.</summary>
        bool TryClaimReconnectSlot();

        void StopConnectionHealthMonitor(string reason);

        void FireAndForgetPairingStage2Reconnect();
    }
}
