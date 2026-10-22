// =============================================================================
// HistoryFirstSyncListHoldDecision
//
// Whether the first-sync SQLite quiet timer may publish IsCompleted (and thus
// release the chat-list hold / DisplayRequest), or must wait for FULL_HISTORY
// catch-up to finish. Completing with zero conversations while pages still
// arrive is the erratic early paint. Once SQLite has conversation rows, unlock
// the list even if catch-up keeps the history banner — otherwise idle-continue
// on tip age alone holds materialized=False indefinitely.
//
// When catch-up is idle, the host still resolves directory names before
// IsCompleted — list unlock shows chats with names, then network photos.
// =============================================================================

namespace Unison.Core.Helpers
{
    public static class HistoryFirstSyncListHoldDecision
    {
        /// <summary>
        /// True when quiet-finalize must keep initial-sync active (no <c>IsCompleted</c>)
        /// because catch-up still owes work while the first-sync list hold is up and SQLite
        /// has not yet written any conversation rows.
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

            // Conversations already landed: paint the list. Catch-up may continue with its banner.
            if (processedConversations > 0)
            {
                return false;
            }

            return handOffCatchUp || catchUpActive || enrichmentAwaitingHeavyHistory;
        }

        /// <summary>
        /// True when the first-sync list may unlock only after directory names are resolved
        /// (catch-up already idle — caller runs names, then publishes <c>IsCompleted</c>).
        /// </summary>
        public static bool ShouldResolveNamesBeforeListRelease(bool firstSyncInProgress)
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
