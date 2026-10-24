// =============================================================================
// IRequestFullHistoryOnDemandTrackedHost
//
// Side effects for RequestFullHistoryOnDemandTrackedDispatch.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.State;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IRequestFullHistoryOnDemandTrackedHost
    {
        TimeSpan FullHistoryCatchUpIdleAbort { get; }

        TimeSpan FullHistoryOnDemandResponseTimeout { get; }

        Task<HistoryFullRequestResult> RequestFullHistoryAsync(
            string reason,
            bool isFreshnessRepair,
            DateTime nowUtc);

        void LogFullHistoryRequested(
            string reason,
            string stanzaId,
            int baselineCount,
            bool isFreshnessRepair);

        void WriteFullHistoryRequestedJournal(
            string reason,
            string stanzaId,
            int baselineCount,
            bool isFreshnessRepair);

        void BeginHistoryCatchUpBanner(string reason);

        void NotifyBackgroundHistorySyncBounced();
    }
}
