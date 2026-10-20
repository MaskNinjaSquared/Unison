// =============================================================================
// HistoryCatchUpLocalWorkPolicy
//
// After reconnect (or a quiet history lot), full-catalog preview reconcile and
// directory enrichment (usync names, groups, network avatars) fight the socket
// for the same scarce Mobile budget the offline / FULL_HISTORY backlog needs.
// While catch-up is still owed, that local work waits; tips still update from
// live/offline apply paths, and enrichment runs once catch-up finishes.
// =============================================================================

namespace Unison.Core.Helpers
{
    public static class HistoryCatchUpLocalWorkPolicy
    {
        /// <summary>
        /// Diagnostic reason when heavy local maintenance should wait, or null when it may run.
        /// </summary>
        public static string DeferReason(bool historyCatchUpActive)
        {
            return historyCatchUpActive ? "history-catch-up" : null;
        }

        /// <summary>
        /// True when full preview reconcile / usync enrichment should step aside for catch-up.
        /// </summary>
        public static bool ShouldDeferHeavyLocalWork(bool historyCatchUpActive)
        {
            return DeferReason(historyCatchUpActive) != null;
        }
    }
}
