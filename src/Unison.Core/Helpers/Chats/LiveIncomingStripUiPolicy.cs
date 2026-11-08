// =============================================================================
// LiveIncomingStripUiPolicy
//
// PreferFrugal live tip paint used to Reposition the catalogue on a Normal hop
// after Urgent tip apply, stacking Move → layout behind the toast path
// (journal: RunLiveStrip 15–28s + strip-reorder seconds). Soft-invalidate the
// visible order instead — same pattern as offline softOrder.
// =============================================================================

namespace Unison.Core.Helpers.Chats
{
    public static class LiveIncomingStripUiPolicy
    {
        public static bool SkipCatalogueReposition(bool preferFrugal) => preferFrugal;
    }
}
