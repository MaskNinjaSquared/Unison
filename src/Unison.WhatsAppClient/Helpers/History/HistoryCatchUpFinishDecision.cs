// =============================================================================
// HistoryCatchUpFinishDecision
//
// After FinishHistoryCatchUpContinue writes the journal and ends the banner:
// unlock first-sync list via names, or schedule enrichment.
// =============================================================================

namespace Unison.WhatsAppClient.Helpers
{
    public enum HistoryCatchUpFinishNext
    {
        /// <summary>releaseEnrichment was false — nothing further.</summary>
        None,

        /// <summary>First-sync hold still up — resolve names then unlock the list.</summary>
        ReleaseListAfterNames,

        /// <summary>Schedule deferred avatars + post-message enrichment.</summary>
        ScheduleEnrichment
    }

    public static class HistoryCatchUpFinishDecision
    {
        public static HistoryCatchUpFinishNext Decide(
            bool releaseEnrichment,
            bool firstSyncListHoldInProgress)
        {
            if (!releaseEnrichment)
            {
                return HistoryCatchUpFinishNext.None;
            }

            if (firstSyncListHoldInProgress)
            {
                return HistoryCatchUpFinishNext.ReleaseListAfterNames;
            }

            return HistoryCatchUpFinishNext.ScheduleEnrichment;
        }
    }
}
