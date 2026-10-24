// =============================================================================
// IFullHistoryOnDemandClearHost
//
// Side effects for FullHistoryOnDemandClearDispatch.
// =============================================================================
using Unison.Core.State;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IFullHistoryOnDemandClearHost
    {
        HistoryOnDemandFullClear ClearFull(out string requestId, out string repairId);

        void LogFullHistoryClear(string message);

        void EndHistoryCatchUpBanner(string reason);
    }
}
