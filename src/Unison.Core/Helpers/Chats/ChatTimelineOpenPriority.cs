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
        /// refresh-visible Continues cannot steal the next few seconds of scroll
        /// frames (large-group open was releasing at 1.8s while Continues still
        /// mutated ~250 rows and starved the dispatcher until the process died).
        /// </summary>
        public static readonly TimeSpan MobileScrollSettleHold = TimeSpan.FromMilliseconds(5000);

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
        /// while the window is hidden (location-keepalive — tip-swapped on a black
        /// CoreWindow looks like a freeze), or while the chat list is cold-filling
        /// VisibleChats from empty (Insert Continues + tip-swapped starved Mobile).
        /// </summary>
        public static bool ShouldPauseTipReconcileUi(
            bool deepLinkListHeld,
            bool timelineLoadPriorityHeld,
            bool windowVisible = true,
            bool listColdPopulate = false)
        {
            return deepLinkListHeld ||
                   timelineLoadPriorityHeld ||
                   !windowVisible ||
                   listColdPopulate;
        }

        /// <summary>
        /// Mobile only: delay Release after Complete so the timeline can absorb the first
        /// scroll gestures before the chat-list resumes budgeted Moves.
        /// </summary>
        public static bool ShouldDelayTimelinePriorityRelease(bool isMobile) => isMobile;

        /// <summary>
        /// Hold must not force a full list rebuild on Release. Only mark dirty when a
        /// budgeted continue or in-flight refresh was interrupted mid-way.
        /// </summary>
        public static bool ShouldMarkVisibleOrderDirtyOnTimelineHold(
            bool hasPendingContinueSource,
            bool isRefreshing) =>
            hasPendingContinueSource || isRefreshing;

        /// <summary>
        /// After Release: only schedule refresh-visible when the catalogue order actually
        /// changed (or a continue was interrupted). Deferred-only is not enough.
        /// </summary>
        public static bool ShouldScheduleVisibleRefreshOnTimelineRelease(bool orderDirty) =>
            orderDirty;
    }
}
