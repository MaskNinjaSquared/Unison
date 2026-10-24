// =============================================================================
// PostMessageEnrichmentGates
//
// Pure decide for TrySchedulePostMessageEnrichment (heavy history / catch-up /
// FULL_HISTORY / list paint).
// =============================================================================
using Unison.Core.Helpers;

namespace Unison.WhatsAppClient.Helpers
{
    public enum PostMessageEnrichmentTryKind
    {
        AlreadyAwaitingHeavyHistory,
        DeferCatchUp,
        ArmAfterFullHistory,
        DeferListPaint,
        Proceed
    }

    public readonly struct PostMessageEnrichmentTryDecision
    {
        public PostMessageEnrichmentTryDecision(
            PostMessageEnrichmentTryKind kind,
            string catchUpDeferReason)
        {
            Kind = kind;
            CatchUpDeferReason = catchUpDeferReason;
        }

        public PostMessageEnrichmentTryKind Kind { get; }

        public string CatchUpDeferReason { get; }
    }

    public static class PostMessageEnrichmentGates
    {
        public static PostMessageEnrichmentTryDecision ClassifyTry(
            bool enrichmentAwaitingHeavyHistory,
            bool historyCatchUpActive,
            bool fullHistoryInFlight,
            bool listPaintPending)
        {
            if (enrichmentAwaitingHeavyHistory)
            {
                return new PostMessageEnrichmentTryDecision(
                    PostMessageEnrichmentTryKind.AlreadyAwaitingHeavyHistory,
                    null);
            }

            string catchUpDeferReason = HistoryCatchUpLocalWorkPolicy.DeferReason(
                historyCatchUpActive);
            if (catchUpDeferReason != null)
            {
                return new PostMessageEnrichmentTryDecision(
                    PostMessageEnrichmentTryKind.DeferCatchUp,
                    catchUpDeferReason);
            }

            if (fullHistoryInFlight)
            {
                return new PostMessageEnrichmentTryDecision(
                    PostMessageEnrichmentTryKind.ArmAfterFullHistory,
                    null);
            }

            if (listPaintPending)
            {
                return new PostMessageEnrichmentTryDecision(
                    PostMessageEnrichmentTryKind.DeferListPaint,
                    null);
            }

            return new PostMessageEnrichmentTryDecision(
                PostMessageEnrichmentTryKind.Proceed,
                null);
        }
    }
}
