// =============================================================================
// ChatTimelineOpenPriority
//
// Pure rules for toast / deep-link chat open: skip the Mobile materialize delay
// and pause tip-reconcile UI slices while the conversation timeline loads.
// =============================================================================
using System;
using Unison.Core.Helpers.Ui;

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
        /// Soft gates (timeline settle / cold populate / window hidden) must not
        /// block tip UI forever — HistoryFacade awaits history-chunk-tip reconcile
        /// before finishing OnDemand apply (journals 8b7b5612 / 68543091: ~72s).
        /// Settle hold is 5s; 8s covers one settle + buffer.
        /// </summary>
        public static readonly TimeSpan UiSlicePauseMaxWait = TimeSpan.FromMilliseconds(8000);

        /// <summary>
        /// ApplyMetadata / listing hops: two Mobile settle holds + buffer, then proceed
        /// so a stuck Hold cannot freeze group apply or starve Normal tip hops.
        /// </summary>
        public static readonly TimeSpan TimelineHoldWaitMax = TimeSpan.FromMilliseconds(12000);

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
        /// CoreWindow looks like a freeze), while the chat list is cold-filling
        /// VisibleChats from empty (Insert Continues + tip-swapped starved Mobile),
        /// or while offline tip flush owns the list (post-replay must not fight tips).
        /// </summary>
        public static bool ShouldPauseTipReconcileUi(
            bool deepLinkListHeld,
            bool timelineLoadPriorityHeld,
            bool windowVisible = true,
            bool listColdPopulate = false,
            bool offlineTipBusy = false)
        {
            return DescribeTipReconcilePause(
                       deepLinkListHeld,
                       timelineLoadPriorityHeld,
                       windowVisible,
                       listColdPopulate,
                       offlineTipBusy) != null;
        }

        /// <summary>
        /// Same rule as <see cref="ShouldPauseTipReconcileUi"/>, naming the gate that holds
        /// tip slices for the journal. Null when nothing pauses them.
        /// Delegates to <see cref="UiWorkHoldPolicy"/> (<see cref="UiWorkKind.TipSlice"/>).
        /// </summary>
        public static string DescribeTipReconcilePause(
            bool deepLinkListHeld,
            bool timelineLoadPriorityHeld,
            bool windowVisible = true,
            bool listColdPopulate = false,
            bool offlineTipBusy = false)
        {
            return UiWorkHoldPolicy.Describe(
                UiWorkKind.TipSlice,
                new UiHoldSnapshot
                {
                    DeepLinkHeld = deepLinkListHeld,
                    TimelineHeld = timelineLoadPriorityHeld,
                    WindowVisible = windowVisible,
                    ColdPopulate = listColdPopulate,
                    OfflineTipBusy = offlineTipBusy,
                });
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

        /// <summary>
        /// Tip-only hydrate must not Move VisibleChats while chat-open holds the gate —
        /// those Moves recycle list containers under the detail and freeze Mobile.
        /// </summary>
        public static bool ShouldReorderVisibleOnTipHydrate(bool timelineLoadPriorityHeld) =>
            HistoryListUiRefresh.ShouldReorderVisibleOnTipHydrate(timelineLoadPriorityHeld);

        /// <summary>
        /// Hot tip work that blocks chat-open / OnDemand apply must use the Normal UI
        /// lane — listing and alias follow-up sit on Low and starved tip slices for ~72s
        /// (ui-slice-slow after pause cleared). Full catalogue heals stay on Low.
        /// </summary>
        public static bool ShouldUseNormalTipUiLane(string reason, int workCount)
        {
            if (workCount <= 0)
            {
                return true;
            }

            if (string.Equals(reason, "chat-open", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(reason, "history-chunk-tip", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Live strip / single-row bumps — same size as a frugal tip slice.
            return workCount <= 10;
        }
    }
}
