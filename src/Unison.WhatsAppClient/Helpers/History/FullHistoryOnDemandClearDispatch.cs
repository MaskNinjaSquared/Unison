// =============================================================================
// FullHistoryOnDemandClearDispatch
//
// Body of ClearFullHistoryOnDemandRequestState. Registry clear + banner end
// stay on the host.
// =============================================================================
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class FullHistoryOnDemandClearDispatch
    {
        public static void Run(IFullHistoryOnDemandClearHost host, string reason)
        {
            if (host == null)
            {
                return;
            }

            switch (host.ClearFull(out string requestId, out string repairId))
            {
                case HistoryOnDemandFullClear.Tracked:
                    host.LogFullHistoryClear(
                        "Cleared full-history request state after " + reason +
                        ": requestId=" + requestId);
                    host.EndHistoryCatchUpBanner("clear-full-history:" + reason);
                    return;

                case HistoryOnDemandFullClear.Stale:
                    host.LogFullHistoryClear(
                        "Cleared stale full-history request flags after " + reason +
                        ": requestId=" + (requestId ?? "<none>") +
                        ", repairId=" + (repairId ?? "<none>"));
                    host.EndHistoryCatchUpBanner("clear-full-history:" + reason);
                    return;
            }
        }
    }
}
