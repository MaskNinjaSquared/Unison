// =============================================================================
// IHydrationFallbackPipelineHost
//
// Side effects for HydrationFallbackPipelineDispatch.
// =============================================================================
using System;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IHydrationFallbackPipelineHost
    {
        bool IsHistoryHydrationActive { get; }

        bool TryGetFreshnessStaleReason(DateTime nowUtc, out string staleReason);

        void NoteHydrationDayAttempt(DateTime nowUtc);

        void WriteHydrationFallbackStart(string reason, string staleReason);

        Task<int> StartBackgroundHistoryBackfillAsync(string reason);

        void FireAndForgetReconnectHistoryCatchUp(string reason);

        void ScheduleHydrationDayRetryIfNeeded(string reason);

        void LogHydrationFallback(string message);

        void RecordHydrationFallbackException(Exception error, string reason);
    }
}
