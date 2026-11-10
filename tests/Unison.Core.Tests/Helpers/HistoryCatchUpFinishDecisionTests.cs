using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class HistoryCatchUpFinishDecisionTests
    {
        [Fact]
        public void None_when_enrichment_not_released()
        {
            Assert.Equal(
                HistoryCatchUpFinishNext.None,
                HistoryCatchUpFinishDecision.Decide(false, true));
        }

        [Fact]
        public void Names_when_first_sync_hold_up()
        {
            Assert.Equal(
                HistoryCatchUpFinishNext.ReleaseListAfterNames,
                HistoryCatchUpFinishDecision.Decide(true, true));
        }

        [Fact]
        public void Enrichment_when_hold_idle()
        {
            Assert.Equal(
                HistoryCatchUpFinishNext.ScheduleEnrichment,
                HistoryCatchUpFinishDecision.Decide(true, false));
        }

        [Fact]
        public void List_still_awaiting_first_history_is_released_then_enriched()
        {
            Assert.Equal(
                HistoryCatchUpFinishNext.ReleaseListThenEnrichment,
                HistoryCatchUpFinishDecision.Decide(true, false, listAwaitingFirstHistory: true));
        }

        [Fact]
        public void First_sync_hold_wins_over_awaiting_first_history()
        {
            Assert.Equal(
                HistoryCatchUpFinishNext.ReleaseListAfterNames,
                HistoryCatchUpFinishDecision.Decide(true, true, listAwaitingFirstHistory: true));
        }

        [Fact]
        public void Awaiting_first_history_without_release_does_nothing()
        {
            Assert.Equal(
                HistoryCatchUpFinishNext.None,
                HistoryCatchUpFinishDecision.Decide(false, false, listAwaitingFirstHistory: true));
        }

        [Fact]
        public void Pairing_bootstrap_with_chunk_seen_does_not_unlock_list()
        {
            Assert.Equal(
                HistoryCatchUpFinishNext.None,
                HistoryCatchUpFinishDecision.Decide(
                    true,
                    firstSyncListHoldInProgress: false,
                    listAwaitingFirstHistory: true,
                    pairingBootstrapChunkSeenOrApplying: true));
        }

        [Fact]
        public void Pairing_bootstrap_hold_with_chunk_applying_does_not_unlock_list()
        {
            Assert.Equal(
                HistoryCatchUpFinishNext.None,
                HistoryCatchUpFinishDecision.Decide(
                    true,
                    firstSyncListHoldInProgress: true,
                    listAwaitingFirstHistory: true,
                    pairingBootstrapChunkSeenOrApplying: true));
        }

        [Fact]
        public void Pairing_awaiting_without_chunk_still_releases_then_enriches()
        {
            Assert.Equal(
                HistoryCatchUpFinishNext.ReleaseListThenEnrichment,
                HistoryCatchUpFinishDecision.Decide(
                    true,
                    firstSyncListHoldInProgress: false,
                    listAwaitingFirstHistory: true,
                    pairingBootstrapChunkSeenOrApplying: false));
        }

        [Fact]
        public void Deferred_release_escapes_when_apply_idle_after_catch_up()
        {
            Assert.Equal(
                HistoryCatchUpFinishNext.ReleaseListAfterNames,
                HistoryCatchUpFinishDecision.Decide(
                    true,
                    firstSyncListHoldInProgress: true,
                    listAwaitingFirstHistory: true,
                    pairingBootstrapChunkSeenOrApplying: true,
                    firstSyncListReleaseDeferred: true,
                    historyApplyBusy: false));
        }

        [Fact]
        public void Deferred_release_stays_held_while_apply_busy()
        {
            Assert.Equal(
                HistoryCatchUpFinishNext.None,
                HistoryCatchUpFinishDecision.Decide(
                    true,
                    firstSyncListHoldInProgress: true,
                    listAwaitingFirstHistory: true,
                    pairingBootstrapChunkSeenOrApplying: true,
                    firstSyncListReleaseDeferred: true,
                    historyApplyBusy: true));
        }
    }
}
