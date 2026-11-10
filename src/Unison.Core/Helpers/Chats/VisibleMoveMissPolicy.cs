// =============================================================================
// VisibleMoveMissPolicy
//
// PreferFrugal Move-miss (and tip-driven order dirt) used to ScheduleRefresh →
// BuildVisibleRefreshSource. When the painted list is already the unfiltered
// catalogue, reorder in place instead — same end state, no rebuild storm.
// =============================================================================

namespace Unison.Core.Helpers.Chats
{
    public static class VisibleMoveMissPolicy
    {
        /// <summary>
        /// True when frugal UI should drain/reorder VisibleChats instead of rebuilding.
        /// </summary>
        public static bool PreferInPlaceReorder(
            bool preferFrugal,
            int visibleCount,
            bool filterIsAll,
            bool searchEmpty)
        {
            return preferFrugal &&
                visibleCount > 0 &&
                filterIsAll &&
                searchEmpty;
        }
    }
}
