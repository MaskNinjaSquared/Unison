using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class HistoryFirstSyncListHoldDecisionTests
    {
        [Fact]
        public void Quiet_with_conversations_unlocks_even_while_catch_up_active()
        {
            Assert.False(HistoryFirstSyncListHoldDecision.ShouldDeferInitialSyncCompleted(
                firstSyncInProgress: true,
                handOffCatchUp: true,
                catchUpActive: true,
                enrichmentAwaitingHeavyHistory: true,
                processedConversations: 12));
        }

        [Fact]
        public void Quiet_with_empty_store_still_defers_while_catch_up_handoff()
        {
            Assert.True(HistoryFirstSyncListHoldDecision.ShouldDeferInitialSyncCompleted(
                firstSyncInProgress: true,
                handOffCatchUp: true,
                catchUpActive: false,
                enrichmentAwaitingHeavyHistory: false,
                processedConversations: 0));
        }

        [Fact]
        public void Quiet_with_empty_store_defers_while_catch_up_banner_active()
        {
            Assert.True(HistoryFirstSyncListHoldDecision.ShouldDeferInitialSyncCompleted(
                firstSyncInProgress: true,
                handOffCatchUp: false,
                catchUpActive: true,
                enrichmentAwaitingHeavyHistory: false,
                processedConversations: 0));
        }

        [Fact]
        public void Quiet_with_empty_store_defers_while_enrichment_awaits_heavy_history()
        {
            Assert.True(HistoryFirstSyncListHoldDecision.ShouldDeferInitialSyncCompleted(
                firstSyncInProgress: true,
                handOffCatchUp: false,
                catchUpActive: false,
                enrichmentAwaitingHeavyHistory: true,
                processedConversations: 0));
        }

        [Fact]
        public void Quiet_without_catch_up_does_not_defer_and_schedules_names_after_unlock()
        {
            Assert.False(HistoryFirstSyncListHoldDecision.ShouldDeferInitialSyncCompleted(
                firstSyncInProgress: true,
                handOffCatchUp: false,
                catchUpActive: false,
                enrichmentAwaitingHeavyHistory: false,
                processedConversations: 4));
            Assert.False(HistoryFirstSyncListHoldDecision.ShouldResolveNamesBeforeListRelease(
                firstSyncInProgress: true));
            Assert.True(HistoryFirstSyncListHoldDecision.ShouldScheduleNamesAfterListRelease(
                firstSyncInProgress: true));
        }

        [Fact]
        public void After_list_already_released_catch_up_does_not_defer_or_schedule_names()
        {
            Assert.False(HistoryFirstSyncListHoldDecision.ShouldDeferInitialSyncCompleted(
                firstSyncInProgress: false,
                handOffCatchUp: true,
                catchUpActive: true,
                enrichmentAwaitingHeavyHistory: true,
                processedConversations: 0));
            Assert.False(HistoryFirstSyncListHoldDecision.ShouldResolveNamesBeforeListRelease(
                firstSyncInProgress: false));
            Assert.False(HistoryFirstSyncListHoldDecision.ShouldScheduleNamesAfterListRelease(
                firstSyncInProgress: false));
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
