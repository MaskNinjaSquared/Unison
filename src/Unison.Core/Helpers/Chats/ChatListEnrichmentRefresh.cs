// =============================================================================
// ChatListEnrichmentRefresh
//
// Frugal Mobile must not run BuildVisibleRefreshSource on the UI thread while
// names/avatars/groups enrichment still owns the dispatcher. Attach used to
// call RefreshVisibleChats synchronously mid-groups (fb466dc2: buildMs≈411s,
// ui-stall active=none). Defer until the phase clears; if VisibleChats is still
// empty then, force a full refresh rather than an in-place reorder no-op.
// =============================================================================
namespace Unison.Core.Helpers.Chats
{
    public static class ChatListEnrichmentRefresh
    {
        /// <summary>
        /// True when Attach / schedule should mark order dirty and skip the cold build
        /// until enrichment ends.
        /// </summary>
        public static bool ShouldDeferVisibleRefresh(bool preferFrugalUi, bool enrichmentPhase) =>
            preferFrugalUi && enrichmentPhase;

        /// <summary>
        /// After enrichment clears: empty list needs a full refresh (deferred cold attach);
        /// a painted list only needs reorder / optional rebuild when order is dirty.
        /// </summary>
        public static bool RequiresFullRefreshAfterEnrichment(int visibleCount, bool orderDirty) =>
            orderDirty && visibleCount <= 0;
    }
}
