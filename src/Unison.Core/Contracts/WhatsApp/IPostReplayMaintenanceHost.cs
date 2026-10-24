// =============================================================================
// IPostReplayMaintenanceHost
//
// Side effects for PostReplayMaintenanceDispatch (post-offline / session-init
// list repair + enrichment arm).
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Unison.Core.Contracts.WhatsApp
{
    public interface IPostReplayMaintenanceHost
    {
        bool UseFrugalSyncBudget { get; }

        bool IsConnected { get; }

        bool IsWindowVisible { get; }

        bool IsHistoryCatchUpActive { get; }

        bool IsListPaintPending { get; }

        Task<bool> WaitForStartupQuietAsync(TimeSpan timeout, CancellationToken token);

        Task<bool> WaitForMemoryHeadroomAsync(CancellationToken token);

        void ClearSyncStatus();

        void WriteMemorySkipped();

        void MarkEnrichmentAwaitingHeavyHistory();

        void WriteDeferred(string catchUpDeferReason, int offlineCount);

        void WriteListPaintDeferred();

        Task NotifyPostReplayMaintenanceAsync(
            int offlineCount,
            bool runDelayedBulk,
            CancellationToken token);

        void WriteComplete(int offlineCount);

        void TrySchedulePostMessageEnrichment(string reason);

        void RecordFailed(Exception ex, int offlineCount);
    }
}