// =============================================================================
// HistoryCatchUpFinishDecision
//
// After FinishHistoryCatchUpContinue writes the journal and ends the banner:
// unlock first-sync list via names, or schedule enrichment.
//
// A fresh pairing arms the list hold before any chunk exists. When the phone
// never sends history, no chunk ever completes the first sync, so catch-up
// finish is the only point left to release the list (live chats) — otherwise
// the list waits on history and enrichment waits on the list paint.
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
        ScheduleEnrichment,

        /// <summary>
        /// No history chunk ever started the first sync, yet the list still waits on it —
        /// complete the first sync (list paints the live rows), then schedule enrichment.
        /// </summary>
        ReleaseListThenEnrichment
    }

    public static class HistoryCatchUpFinishDecision
    {
        public static HistoryCatchUpFinishNext Decide(
            bool releaseEnrichment,
            bool firstSyncListHoldInProgress,
            bool listAwaitingFirstHistory = false)
        {
            if (!releaseEnrichment)
            {
                return HistoryCatchUpFinishNext.None;
            }

            if (firstSyncListHoldInProgress)
            {
                return HistoryCatchUpFinishNext.ReleaseListAfterNames;
            }

            if (listAwaitingFirstHistory)
            {
                return HistoryCatchUpFinishNext.ReleaseListThenEnrichment;
            }

            return HistoryCatchUpFinishNext.ScheduleEnrichment;
        }
    }
}
