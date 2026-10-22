using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class HistoryFirstSyncListHoldDecisionTests
    {
        [Fact]
        public void Quiet_with_catch_up_handoff_defers_while_first_sync_hold_is_up_and_sqlite_empty()
        {
            Assert.True(HistoryFirstSyncListHoldDecision.ShouldDeferInitialSyncCompleted(
                firstSyncInProgress: true,
                handOffCatchUp: true,
                catchUpActive: false,
                enrichmentAwaitingHeavyHistory: false,
                processedConversations: 0));
        }

        [Fact]
        public void Quiet_with_active_catch_up_banner_defers_when_sqlite_still_empty()
        {
            Assert.True(HistoryFirstSyncListHoldDecision.ShouldDeferInitialSyncCompleted(
                firstSyncInProgress: true,
                handOffCatchUp: false,
                catchUpActive: true,
                enrichmentAwaitingHeavyHistory: false,
                processedConversations: 0));
        }

        [Fact]
        public void Quiet_while_enrichment_awaits_heavy_history_defers_when_sqlite_still_empty()
        {
            Assert.True(HistoryFirstSyncListHoldDecision.ShouldDeferInitialSyncCompleted(
                firstSyncInProgress: true,
                handOffCatchUp: false,
                catchUpActive: false,
                enrichmentAwaitingHeavyHistory: true,
                processedConversations: 0));
        }

        [Fact]
        public void Quiet_without_catch_up_leaves_catch_up_defer_false_then_names_before_release()
        {
            Assert.False(HistoryFirstSyncListHoldDecision.ShouldDeferInitialSyncCompleted(
                firstSyncInProgress: true,
                handOffCatchUp: false,
                catchUpActive: false,
                enrichmentAwaitingHeavyHistory: false,
                processedConversations: 0));
            Assert.True(HistoryFirstSyncListHoldDecision.ShouldResolveNamesBeforeListRelease(
                firstSyncInProgress: true));
        }

        [Fact]
        public void After_list_already_released_catch_up_does_not_defer_completed()
        {
            Assert.False(HistoryFirstSyncListHoldDecision.ShouldDeferInitialSyncCompleted(
                firstSyncInProgress: false,
                handOffCatchUp: true,
                catchUpActive: true,
                enrichmentAwaitingHeavyHistory: true,
                processedConversations: 40));
            Assert.False(HistoryFirstSyncListHoldDecision.ShouldResolveNamesBeforeListRelease(
                firstSyncInProgress: false));
        }

        [Fact]
        public void Quiet_with_sqlite_conversations_does_not_defer_even_while_catch_up_continues()
        {
            // Diagnostic BO: FULL_HISTORY idle-continue on tip age held the list forever
            // (materialized=False) after 175 conversations were already in SQLite.
            Assert.False(HistoryFirstSyncListHoldDecision.ShouldDeferInitialSyncCompleted(
                firstSyncInProgress: true,
                handOffCatchUp: true,
                catchUpActive: true,
                enrichmentAwaitingHeavyHistory: true,
                processedConversations: 175));
        }

        [Fact]
        public void Empty_quiet_lot_during_first_sync_does_not_finish_catch_up()
        {
            Assert.False(HistoryFirstSyncListHoldDecision.ShouldFinishCatchUpOnEmptyQuietLot(
                firstSyncInProgress: true,
                processedConversations: 0));
        }

        [Fact]
        public void Empty_quiet_lot_after_list_released_finishes_catch_up()
        {
            Assert.True(HistoryFirstSyncListHoldDecision.ShouldFinishCatchUpOnEmptyQuietLot(
                firstSyncInProgress: false,
                processedConversations: 0));
        }

        [Fact]
        public void Quiet_lot_with_conversations_is_not_empty_finish()
        {
            Assert.False(HistoryFirstSyncListHoldDecision.ShouldFinishCatchUpOnEmptyQuietLot(
                firstSyncInProgress: true,
                processedConversations: 12));
            Assert.False(HistoryFirstSyncListHoldDecision.ShouldFinishCatchUpOnEmptyQuietLot(
                firstSyncInProgress: false,
                processedConversations: 12));
        }
    }
}
