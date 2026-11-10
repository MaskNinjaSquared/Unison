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

        /// <summary>
        /// PairingBootstrap owns history — defer message-store-epoch FULL_HISTORY.
        /// </summary>
        bool IsPairingBootstrapActive { get; }

        void LogForceHistoryRepair(string message);

        Task<bool> RequestFullHistoryOnDemandTrackedAsync(string reason, bool isFreshnessRepair);

        void ClearForceHistoryRepairPending();
    }
}
