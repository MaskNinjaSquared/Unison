// =============================================================================
// HistoryFirstSyncListHoldDecision
//
// Whether the first-sync SQLite quiet timer may publish IsCompleted (and thus
// release the chat-list hold / DisplayRequest), or must wait. Completing with
// zero conversations while catch-up pages still arrive is the erratic early
// paint. Once SQLite has conversation rows, unlock the list — catch-up may keep
// its banner, and directory names resolve in the background after unlock.
// =============================================================================

namespace Unison.WhatsAppClient.Helpers
{
    public static class HistoryFirstSyncListHoldDecision
    {
        /// <summary>
        /// True when quiet-finalize must keep initial-sync active (no <c>IsCompleted</c>)
        /// because catch-up still owes work and the store has no conversations yet.
        /// Enrichment awaiting heavy history is not a defer gate — it is a consumer of
        /// unlock; catch-up hand-off / banner already cover "Bootstrap still coming".
        /// </summary>
        public static bool ShouldDeferInitialSyncCompleted(
            bool firstSyncInProgress,
            bool handOffCatchUp,
            bool catchUpActive,
            bool enrichmentAwaitingHeavyHistory,
            int processedConversations)
        {
            if (!firstSyncInProgress)
            {
                return false;
            }

            // Rows already on disk — unlock the list; catch-up / names continue.
            if (processedConversations > 0)
            {
                return false;
            }

            // enrichmentAwaitingHeavyHistory is intentionally unused: keeping it as a
            // parameter preserves call-site ABI while catch-up owns the empty-store hold.
            _ = enrichmentAwaitingHeavyHistory;
            return handOffCatchUp || catchUpActive;
        }

        /// <summary>
        /// Legacy gate: names must not block list unlock. Prefer
        /// <see cref="ShouldScheduleNamesAfterListRelease"/>.
        /// </summary>
        public static bool ShouldResolveNamesBeforeListRelease(bool firstSyncInProgress)
        {
            return false;
        }

        /// <summary>
        /// True when directory names should run after <c>IsCompleted</c> (list already unlocked).
        /// </summary>
        public static bool ShouldScheduleNamesAfterListRelease(bool firstSyncInProgress)
        {
            return firstSyncInProgress;
        }

        /// <summary>
        /// Empty StatusV3 quiet must not finish catch-up while the first-sync hold is up —
        /// Bootstrap/Recent may still be inbound. Outside first-sync, an empty lot means done.
        /// </summary>
        public static bool ShouldFinishCatchUpOnEmptyQuietLot(
            bool firstSyncInProgress,
            int processedConversations)
        {
            if (processedConversations > 0)
            {
                return false;
            }

            return !firstSyncInProgress;
        }
    }
}
