// =============================================================================
// IForceHistoryRepairConsumeHost
//
// Side effects for ForceHistoryRepairConsumeDispatch.
// =============================================================================
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IForceHistoryRepairConsumeHost
    {
        bool TryReadForceHistoryRepairPending();

        void LogForceHistoryRepair(string message);

        Task<bool> RequestFullHistoryOnDemandTrackedAsync(string reason, bool isFreshnessRepair);

        void ClearForceHistoryRepairPending();
    }
}
