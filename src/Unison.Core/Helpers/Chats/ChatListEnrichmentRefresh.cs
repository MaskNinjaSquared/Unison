// =============================================================================
// ChatListEnrichmentRefresh
//
// Frugal Mobile must not run the heavy Active BuildVisibleRefreshSource on the
// UI thread while names/avatars/groups enrichment still owns the dispatcher.
// Channels/Archived use the lean catalog path and never defer for enrichment.
// =============================================================================
using System;
using Unison.Core.Models;

namespace Unison.Core.Helpers.Chats
{
    public static class ChatListEnrichmentRefresh
    {
        /// <summary>
        /// True when Attach / schedule should mark order dirty and skip the cold build
        /// until enrichment ends (Active + frugal only).
        /// </summary>
        public static bool ShouldDeferVisibleRefresh(
            bool preferFrugalUi,
            bool enrichmentPhase,
            ChatListScope scope = ChatListScope.Active) =>
            ChatListCatalogAccess.ShouldDeferAttachRefresh(
                preferFrugalUi,
                enrichmentPhase,
                scope);

        /// <summary>
        /// After enrichment clears: empty list needs a full refresh (deferred cold attach);
        /// a painted list only needs reorder / optional rebuild when order is dirty.
        /// </summary>
        public static bool RequiresFullRefreshAfterEnrichment(int visibleCount, bool orderDirty) =>
            orderDirty && visibleCount <= 0;

        /// <summary>
        /// ChatStore warm → pin ApplyTo must not cold-rebuild Active while Attach already
        /// deferred for enrichment (Settings go-back / list unload mid-groups).
        /// </summary>
        public static bool ShouldDeferPinsVisibleRefresh(
            bool refreshAlreadyDeferredForEnrichment,
            bool preferFrugalUi,
            bool enrichmentPhase,
            ChatListScope scope = ChatListScope.Active) =>
            refreshAlreadyDeferredForEnrichment ||
            ShouldDeferVisibleRefresh(preferFrugalUi, enrichmentPhase, scope);

        /// <summary>
        /// Pins already mutated the ChatItem refs — reshuffle painted rows instead of
        /// BuildVisibleRefreshSource when the ListView still has content.
        /// </summary>
        public static bool PreferInPlaceReorderAfterPins(int visibleCount) =>
            visibleCount > 0;

        /// <summary>
        /// Settings go-back / list unload left VisibleChats empty while Attach defers the
        /// cold rebuild for enrichment — show ChatList_Loading so the pane is not blank.
        /// </summary>
        public static bool ShouldShowLoadingWhileDeferredEmpty(
            int visibleCount,
            bool refreshDeferredForEnrichment) =>
            refreshDeferredForEnrichment && visibleCount <= 0;

        /// <summary>
        /// Empty VisibleChats after unload: schedule one allowDuringEnrichment cold paint so
        /// Settings go-back never spins forever when Groups/Avatars Raise(null) is delayed
        /// or missed while Detached. Mid-enrichment ScheduleRefresh still defers (storm freio).
        /// </summary>
        public static bool ShouldPaintEmptyListDuringEnrichmentDeferral(int visibleCount) =>
            visibleCount <= 0;

        /// <summary>
        /// Quiet window before the empty-deferral cold paint — long enough for ListView Loaded
        /// to bind ItemsSource, short enough that the loading ring is only a flash.
        /// </summary>
        public static readonly TimeSpan EmptyDeferredListPaintDelay =
            TimeSpan.FromMilliseconds(50);
    }
}
