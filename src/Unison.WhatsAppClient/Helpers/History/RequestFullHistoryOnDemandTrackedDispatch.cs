// =============================================================================
// RequestFullHistoryOnDemandTrackedDispatch
//
// Body of RequestFullHistoryOnDemandTrackedAsync after HistoryDispatch send.
// Socket / registry I/O stay on the host.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class RequestFullHistoryOnDemandTrackedDispatch
    {
        public static async Task<bool> RunAsync(
            IRequestFullHistoryOnDemandTrackedHost host,
            string reason,
            bool isFreshnessRepair)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            HistoryFullRequestResult result = await host.RequestFullHistoryAsync(
                    reason,
                    isFreshnessRepair,
                    DateTime.UtcNow)
                .ConfigureAwait(false);
            if (result.Outcome != HistoryFullRequestOutcome.Sent)
            {
                return false;
            }

            string stanzaId = result.RequestId;
            int baselineCount = result.BaselineMessageCount;
            host.LogFullHistoryRequested(reason, stanzaId, baselineCount, isFreshnessRepair);
            host.WriteFullHistoryRequestedJournal(
                reason,
                stanzaId,
                baselineCount,
                isFreshnessRepair);
            if (isFreshnessRepair)
            {
                host.BeginHistoryCatchUpBanner(reason);
            }

            host.NotifyBackgroundHistorySyncBounced();
            return true;
        }
    }
}
