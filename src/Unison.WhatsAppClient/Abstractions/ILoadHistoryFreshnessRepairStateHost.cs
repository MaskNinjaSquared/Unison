// =============================================================================
// ILoadHistoryFreshnessRepairStateHost
//
// Side effects for LoadHistoryFreshnessRepairStateDispatch.
// =============================================================================
using System;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface ILoadHistoryFreshnessRepairStateHost
    {
        string ReadLastFullHistoryRepairCompletedUtcRaw();

        string ReadLastFreshnessReconnectFallbackUtcRaw();

        void SetLastFullHistoryRepairCompletedUtc(DateTime timestampUtc);

        void SetLastFreshnessReconnectFallbackUtc(DateTime timestampUtc);

        void LogLoadedFullHistoryRepairCompleted(DateTime timestampUtc);

        void LogLoadedFreshnessReconnectFallback(DateTime timestampUtc);

        void LogLoadHistoryFreshnessRepairStateFailed(string message);
    }
}
