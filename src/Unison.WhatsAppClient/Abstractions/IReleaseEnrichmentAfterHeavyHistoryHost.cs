// =============================================================================
// IReleaseEnrichmentAfterHeavyHistoryHost
//
// Side effects for ReleaseEnrichmentAfterHeavyHistoryDispatch.
// =============================================================================

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IReleaseEnrichmentAfterHeavyHistoryHost
    {
        bool IsHistoryCatchUpActive { get; }

        void MarkEnrichmentAwaitingHeavyHistory();

        void ClearEnrichmentAwaitingHeavyHistory();

        void TrySchedulePostMessageEnrichment(string reason);

        void LogEnrichmentStillDeferred(string reason);

        void WriteEnrichmentDeferredCatchUp(string reason);
    }
}
