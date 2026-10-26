// =============================================================================
// IReconnectHistoryCatchUpHost
//
// Side effects for ReconnectHistoryCatchUpDispatch.
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IReconnectHistoryCatchUpHost
    {
        bool IsHistoryCatchUpActive { get; }

        bool IsConnected { get; }

        bool IsWindowVisible { get; }

        Task<bool> WaitForStartupQuietAsync(
            TimeSpan timeout,
            string reason,
            CancellationToken token);

        void ReleaseFullHistoryIfUntracked();

        Task<bool> RequestFullHistoryOnDemandTrackedAsync(string reason, bool isFreshnessRepair);

        void ClearEnrichmentAwaitingHeavyHistory();

        void TrySchedulePostMessageEnrichment(string reason);

        void LogReconnectHistoryCatchUp(string message);

        void WriteReconnectHistoryCatchUpJournal(string reason);
    }
}
