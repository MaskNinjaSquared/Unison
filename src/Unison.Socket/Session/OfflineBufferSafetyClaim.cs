// =============================================================================
// OfflineBufferSafetyClaim
//
// Decide whether a buffer safety timeout may claim "pending notifications".
// BeginBuffering runs before TLS; a timeout while !IsConnected must defer the
// claim until the session is actually open, or "synced" never arrives.
// =============================================================================

namespace Unison.Socket.Session
{
    public enum OfflineBufferSafetyOutcome
    {
        None = 0,
        Deferred,
        ClaimPendingNotifications,
        SkippedAlreadyReceived,
        SkippedNotPending
    }

    public sealed class OfflineBufferSafetyClaim
    {
        private bool _pendingDeferred;

        public bool ReceivedPendingNotifications { get; private set; }

        public bool HasDeferredClaim => _pendingDeferred;

        public OfflineBufferSafetyOutcome OnSafetyTimeout(bool isConnected)
        {
            if (ReceivedPendingNotifications)
            {
                return OfflineBufferSafetyOutcome.SkippedAlreadyReceived;
            }

            if (!isConnected)
            {
                _pendingDeferred = true;
                return OfflineBufferSafetyOutcome.Deferred;
            }

            ReceivedPendingNotifications = true;
            _pendingDeferred = false;
            return OfflineBufferSafetyOutcome.ClaimPendingNotifications;
        }

        public OfflineBufferSafetyOutcome OnConnected()
        {
            if (!_pendingDeferred)
            {
                return OfflineBufferSafetyOutcome.SkippedNotPending;
            }

            _pendingDeferred = false;
            if (ReceivedPendingNotifications)
            {
                return OfflineBufferSafetyOutcome.SkippedAlreadyReceived;
            }

            ReceivedPendingNotifications = true;
            return OfflineBufferSafetyOutcome.ClaimPendingNotifications;
        }

        public void MarkReceivedFromServer()
        {
            ReceivedPendingNotifications = true;
            _pendingDeferred = false;
        }
    }
}
