// =============================================================================
// ChatTimelineOpenPriority
//
// Pure rules for toast / deep-link chat open: skip the Mobile materialize delay
// and pause tip-reconcile UI slices while the conversation timeline loads.
// =============================================================================
using System;

namespace Unison.Core.Helpers.Chats
{
    public static class ChatTimelineOpenPriority
    {
        /// <summary>
        /// After timeline paint on Mobile, keep the priority gate held so list
        /// refresh-visible Continues cannot steal the next 1–2s of scroll frames.
        /// </summary>
        public static readonly TimeSpan MobileScrollSettleHold = TimeSpan.FromMilliseconds(1800);

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
        /// Tip reconcile UI slices must wait while deep-link holds the list, while
        /// CompleteActiveChatLoadAsync owns the dispatcher for SQLite + bubble paint,
        /// or while the window is hidden (location-keepalive keeps the process alive —
        /// tip-swapped / refresh-visible on a black CoreWindow looks like a freeze).
        /// </summary>
        public static bool ShouldPauseTipReconcileUi(
            bool deepLinkListHeld,
            bool timelineLoadPriorityHeld,
            bool windowVisible = true)
        {
            return deepLinkListHeld || timelineLoadPriorityHeld || !windowVisible;
        }

        /// <summary>
        /// Mobile only: delay Release after Complete so the timeline can absorb the first
        /// scroll gestures before the chat-list resumes budgeted Moves.
        /// </summary>
        public static bool ShouldDelayTimelinePriorityRelease(bool isMobile) => isMobile;
    }
}
