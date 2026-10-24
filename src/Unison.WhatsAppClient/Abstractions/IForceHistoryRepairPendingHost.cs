// =============================================================================
// IForceHistoryRepairPendingHost
//
// Side effects for ForceHistoryRepairPendingDispatch.
// =============================================================================

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IForceHistoryRepairPendingHost
    {
        void SetForceHistoryRepairPending();

        void LogForceHistoryRepairPending(string message);

        void WriteForceHistoryRepairLatched(string reason);
    }
}
