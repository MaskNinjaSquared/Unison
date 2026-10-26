// =============================================================================
// ICompleteUserResyncHistoryWaitHost
//
// Side effects for CompleteUserResyncHistoryWaitDispatch.
// =============================================================================

namespace Unison.WhatsAppClient.Abstractions
{
    public interface ICompleteUserResyncHistoryWaitHost
    {
        /// <summary>True when a waiter was present and claimed.</summary>
        bool TryClaimUserResyncHistoryWaiter(out object waiter);

        void ClearForceHistoryRepairPendingBestEffort();

        void LogUserResyncHistoryWaitCompleted(string reason);

        void CompleteUserResyncHistoryWaiter(object waiter);
    }
}
