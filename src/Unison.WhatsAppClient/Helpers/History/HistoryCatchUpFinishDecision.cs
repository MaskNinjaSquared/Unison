// =============================================================================
// HistoryCatchUpFinishDecision
//
// After FinishHistoryCatchUpContinue writes the journal and ends the banner:
// unlock first-sync list via names, or schedule enrichment.
//
// PairingBootstrap owns list unlock via SQLite quiet-finalize once a history
// chunk has been seen or is applying. Catch-up finish must not paint the list
// in that window (ad243a19: empty-ack stagnant released during InitialBootstrap).
//
// Escape: a fresh pairing arms the list hold before any chunk exists. When the
// phone never sends history, catch-up finish is still the only point left to
// release the list (live chats) — otherwise list waits on history and
// enrichment waits on the list paint.
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
            bool listAwaitingFirstHistory = false,
            bool pairingBootstrapChunkSeenOrApplying = false)
        {
            if (!releaseEnrichment)
            {
                return HistoryCatchUpFinishNext.None;
            }

            // PairingBootstrap already received/is applying history — quiet-finalize owns unlock.
            if (pairingBootstrapChunkSeenOrApplying &&
                (listAwaitingFirstHistory || firstSyncListHoldInProgress))
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
