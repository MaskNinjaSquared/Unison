// =============================================================================
// HistoryFirstSyncListHoldDecision
//
// Whether the first-sync SQLite quiet timer may publish IsCompleted (and thus
// release the chat-list hold / DisplayRequest), or must wait for FULL_HISTORY
// catch-up to finish. Completing too early is the erratic 1.8.3-style paint:
// chats appear while pages still arrive in the background with no spinner.
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
        /// because catch-up still owes work while the first-sync list hold is up.
        /// </summary>
        public static bool ShouldDeferInitialSyncCompleted(
            bool firstSyncInProgress,
            bool handOffCatchUp,
            bool catchUpActive,
            bool enrichmentAwaitingHeavyHistory)
        {
            if (!firstSyncInProgress)
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
