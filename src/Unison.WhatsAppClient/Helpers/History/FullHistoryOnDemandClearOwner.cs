// =============================================================================
// FullHistoryOnDemandClearOwner
//
// Owns IFullHistoryOnDemandClearHost: clears the FULL_HISTORY slot on
// HistoryOnDemandRegistry and ends the catch-up banner.
// =============================================================================
using System;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class FullHistoryOnDemandClearOwner : IFullHistoryOnDemandClearHost
    {
        private readonly HistoryOnDemandRegistry _historyRequests;
        private readonly Action<string> _endCatchUpBanner;
        private readonly Action<string> _log;

        public FullHistoryOnDemandClearOwner(
            HistoryOnDemandRegistry historyRequests,
            Action<string> endCatchUpBanner,
            Action<string> log)
        {
            _historyRequests = historyRequests
                ?? throw new ArgumentNullException(nameof(historyRequests));
            _endCatchUpBanner = endCatchUpBanner
                ?? throw new ArgumentNullException(nameof(endCatchUpBanner));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public void Run(string reason) => FullHistoryOnDemandClearDispatch.Run(this, reason);

        HistoryOnDemandFullClear IFullHistoryOnDemandClearHost.ClearFull(
            out string requestId,
            out string repairId) =>
            _historyRequests.ClearFull(out requestId, out repairId);

        void IFullHistoryOnDemandClearHost.LogFullHistoryClear(string message) => _log(message);

        void IFullHistoryOnDemandClearHost.EndHistoryCatchUpBanner(string reason) =>
            _endCatchUpBanner(reason);
    }
}
