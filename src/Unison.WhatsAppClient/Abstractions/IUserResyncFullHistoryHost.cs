// =============================================================================
// IUserResyncFullHistoryHost
//
// Connect / FULL_HISTORY send / force-repair latch for UserResyncFullHistoryDispatch.
// =============================================================================
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IUserResyncFullHistoryHost
    {
        Task EnsureConnectedAsync(int timeoutMs, bool forceFreshTransport);

        Task<bool> RequestFullHistoryOnDemandTrackedAsync(string reason, bool isFreshnessRepair);

        void ReleaseFullHistoryRequestGate();

        bool IsFullHistoryClaimed { get; }

        Task TryConsumeMessageStoreForceHistoryRepairAsync(string reason);

        void LogUserResync(string message);

        void RaiseUserResyncStatus(string status);
    }
}
