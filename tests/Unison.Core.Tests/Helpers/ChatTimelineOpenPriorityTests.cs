// =============================================================================
// Tests for ChatTimelineOpenPriority.
//
// Toast / deep-link must skip Mobile's 2s materialize delay and pause tip UI
// while the conversation timeline owns the dispatcher.
// =============================================================================
using Unison.Core.Helpers.Chats;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ChatTimelineOpenPriorityTests
    {
        [Fact]
        public void Desktop_always_skips_materialize_delay()
        {
            Assert.True(
                ChatTimelineOpenPriority.ShouldSkipMaterializeDelay(
                    isMobile: false,
                    preferFastTimelineLoad: false,
                    deepLinkListHeld: false));
        }

        [Fact]
        public void Mobile_list_open_keeps_materialize_delay()
        {
            Assert.False(
                ChatTimelineOpenPriority.ShouldSkipMaterializeDelay(
                    isMobile: true,
                    preferFastTimelineLoad: false,
                    deepLinkListHeld: false));
        }

        [Fact]
        public void Mobile_toast_prefer_fast_skips_materialize_delay()
        {
            Assert.True(
                ChatTimelineOpenPriority.ShouldSkipMaterializeDelay(
                    isMobile: true,
                    preferFastTimelineLoad: true,
                    deepLinkListHeld: false));
        }

        [Fact]
        public void Mobile_deep_link_hold_skips_materialize_delay()
        {
            Assert.True(
                ChatTimelineOpenPriority.ShouldSkipMaterializeDelay(
                    isMobile: true,
                    preferFastTimelineLoad: false,
                    deepLinkListHeld: true));
        }

        [Fact]
        public void Tip_reconcile_ui_pauses_while_deep_link_held()
        {
            Assert.True(
                ChatTimelineOpenPriority.ShouldPauseTipReconcileUi(
                    deepLinkListHeld: true,
                    timelineLoadPriorityHeld: false));
        }

        [Fact]
        public void Tip_reconcile_ui_pauses_while_timeline_load_held()
        {
            Assert.True(
                ChatTimelineOpenPriority.ShouldPauseTipReconcileUi(
                    deepLinkListHeld: false,
                    timelineLoadPriorityHeld: true));
        }

        [Fact]
        public void Tip_reconcile_ui_runs_when_neither_gate_is_held()
        {
            Assert.False(
                ChatTimelineOpenPriority.ShouldPauseTipReconcileUi(
                    deepLinkListHeld: false,
                    timelineLoadPriorityHeld: false,
                    windowVisible: true));
        }

        [Fact]
        public void Tip_reconcile_ui_pauses_while_window_hidden()
        {
            Assert.True(
                ChatTimelineOpenPriority.ShouldPauseTipReconcileUi(
                    deepLinkListHeld: false,
                    timelineLoadPriorityHeld: false,
                    windowVisible: false));
        }

        [Fact]
        public void Tip_reconcile_ui_pauses_while_list_cold_populate()
        {
            Assert.True(
                ChatTimelineOpenPriority.ShouldPauseTipReconcileUi(
                    deepLinkListHeld: false,
                    timelineLoadPriorityHeld: false,
                    windowVisible: true,
                    listColdPopulate: true));
        }

        [Fact]
        public void Mobile_scroll_settle_hold_covers_large_group_open_window()
        {
            // Must outlast the post-paint Continues storm on a ~250-row list after
            // opening a 500+ member group (1.8s was releasing too early).
            Assert.True(
                ChatTimelineOpenPriority.MobileScrollSettleHold >= TimeSpan.FromSeconds(5));
        }
    }
}
