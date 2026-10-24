// =============================================================================
// ChatTimelineOpenPriority
//
// Pure rules for toast / deep-link chat open: skip the Mobile materialize delay
// and pause tip-reconcile UI slices while the conversation timeline loads.
// =============================================================================
namespace Unison.Core.Helpers.Chats
{
    public static class ChatTimelineOpenPriority
    {
        /// <summary>
        /// Mobile's 2s post-ProgressBar pause was meant for list→detail paint breathing
        /// room. Legacy always skips it via PreferFast; Unison/WhatsApp now do the same
        /// on list tap. Toast / deep-link also skip so tip-swapped cannot starve LoadMessages.
        /// </summary>
        public static bool ShouldSkipMaterializeDelay(
            bool isMobile,
            bool preferFastTimelineLoad,
            bool deepLinkListHeld)
        {
            if (!isMobile)
            {
                return true;
            }

            return preferFastTimelineLoad || deepLinkListHeld;
        }

        /// <summary>
        /// Tip reconcile UI slices must wait while deep-link holds the list or while
        /// CompleteActiveChatLoadAsync owns the dispatcher for SQLite + bubble paint.
        /// </summary>
        public static bool ShouldPauseTipReconcileUi(
            bool deepLinkListHeld,
            bool timelineLoadPriorityHeld)
        {
            return deepLinkListHeld || timelineLoadPriorityHeld;
        }
    }
}
