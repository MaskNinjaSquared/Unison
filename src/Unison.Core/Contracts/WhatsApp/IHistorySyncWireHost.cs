// =============================================================================
// IHistorySyncWireHost
//
// Side effects for HistorySyncWireApply (OnHistorySyncReceived).
// =============================================================================
using System.Threading.Tasks;
using Proto;

namespace Unison.Core.Contracts.WhatsApp
{
    public interface IHistorySyncWireHost
    {
        bool FatalSessionEnded { get; }

        bool HasAuthState { get; }

        void WriteSyncApplyBegin(int conversations, string syncType);

        void WriteSyncApplyEnd(int conversations, long elapsedMs);

        Task ApplyHistorySyncAsync(HistorySync sync);
    }
}
