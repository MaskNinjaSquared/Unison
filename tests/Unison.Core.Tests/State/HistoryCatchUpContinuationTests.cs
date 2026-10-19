// =============================================================================
// Tests for HistoryCatchUpContinuation.
//
// The paginated FULL_HISTORY catch-up keeps asking for the next lot until the
// phone stops sending. Stopping too early leaves old conversations missing;
// never stopping keeps the banner up and the enrichment gate shut forever.
// =============================================================================
using System;
using Unison.Core.Helpers;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State
{
    public class HistoryCatchUpContinuationTests
    {
        private const int StagnantStop = 2;
        private const int MaxRounds = 60;
        private static readonly DateTime T0 = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);

        // --- Active ------------------------------------------------------------

        [Fact]
        public void A_fresh_continuation_is_idle_without_the_banner()
        {
            var continuation = new HistoryCatchUpContinuation();

            Assert.False(continuation.IsActive(bannerActive: false));
            Assert.True(continuation.IsActive(bannerActive: true));
            Assert.Equal(0, continuation.Snapshot().Round);
            Assert.Equal(0, continuation.Snapshot().StagnantCycles);
            Assert.Equal(DateTime.MinValue, continuation.Snapshot().WatermarkUtc);
        }

        [Fact]
        public void A_round_in_progress_keeps_catch_up_active_after_the_banner_ends()
        {
            var continuation = new HistoryCatchUpContinuation();

            continuation.DecideAfterQuietLot(T0, 1, true, StagnantStop, MaxRounds, out _);

            Assert.True(continuation.IsActive(bannerActive: false));
        }

        // --- Quiet lot ---------------------------------------------------------

        [Fact]
        public void A_lot_with_progress_asks_for_the_next_page_and_moves_the_watermark()
        {
            var continuation = new HistoryCatchUpContinuation();

            HistoryCatchUpContinueResult decision =
                continuation.DecideAfterQuietLot(T0, 3, true, StagnantStop, MaxRounds, out HistoryCatchUpProgress before);

            Assert.Equal(HistoryCatchUpContinueAction.Continue, decision.Action);
            Assert.Equal(0, before.Round);
            Assert.Equal(DateTime.MinValue, before.WatermarkUtc);
            HistoryCatchUpProgress after = continuation.Snapshot();
            Assert.Equal(1, after.Round);
            Assert.Equal(0, after.StagnantCycles);
            Assert.Equal(T0, after.WatermarkUtc);
        }

        [Fact]
        public void The_before_snapshot_reports_the_state_the_decision_was_made_from()
        {
            var continuation = new HistoryCatchUpContinuation();
            continuation.DecideAfterQuietLot(T0, 1, true, StagnantStop, MaxRounds, out _);
            continuation.DecideAfterQuietLot(T0, 0, true, StagnantStop, MaxRounds, out _);

            continuation.DecideAfterQuietLot(T0, 0, true, 5, MaxRounds, out HistoryCatchUpProgress before);

            Assert.Equal(2, before.Round);
            Assert.Equal(1, before.StagnantCycles);
            Assert.Equal(T0, before.WatermarkUtc);
        }

        [Fact]
        public void Stagnant_lots_finish_without_resetting_the_round()
        {
            var continuation = new HistoryCatchUpContinuation();
            continuation.DecideAfterQuietLot(T0, 1, true, StagnantStop, MaxRounds, out _);
            continuation.DecideAfterQuietLot(T0, 0, true, StagnantStop, MaxRounds, out _);

            HistoryCatchUpContinueResult decision =
                continuation.DecideAfterQuietLot(T0, 0, true, StagnantStop, MaxRounds, out _);

            Assert.Equal(HistoryCatchUpContinueAction.FinishStagnantProgress, decision.Action);
            HistoryCatchUpProgress after = continuation.Snapshot();
            Assert.Equal(2, after.Round);
            Assert.Equal(2, after.StagnantCycles);
        }

        [Fact]
        public void The_round_ceiling_finishes_the_catch_up()
        {
            var continuation = new HistoryCatchUpContinuation();
            continuation.DecideAfterQuietLot(T0, 1, true, StagnantStop, maxRounds: 1, out _);

            HistoryCatchUpContinueResult decision =
                continuation.DecideAfterQuietLot(T0.AddMinutes(1), 1, true, StagnantStop, maxRounds: 1, out _);

            Assert.Equal(HistoryCatchUpContinueAction.FinishMaxContinues, decision.Action);
            Assert.Equal(1, continuation.Snapshot().Round);
        }

        // --- Offline drain with a fresh tip -----------------------------------

        [Fact]
        public void A_drain_that_brought_messages_continues_and_clears_the_stagnant_count()
        {
            var continuation = new HistoryCatchUpContinuation();
            continuation.DecideAfterFreshDrain(0, 5, out _);

            HistoryCatchUpDrainAction action = continuation.DecideAfterFreshDrain(4, 5, out int stagnant);

            Assert.Equal(HistoryCatchUpDrainAction.Continue, action);
            Assert.Equal(0, stagnant);
            Assert.Equal(0, continuation.Snapshot().StagnantCycles);
        }

        [Fact]
        public void An_empty_drain_retries_until_the_stagnant_stop()
        {
            var continuation = new HistoryCatchUpContinuation();

            HistoryCatchUpDrainAction first = continuation.DecideAfterFreshDrain(0, StagnantStop, out int firstStagnant);
            HistoryCatchUpDrainAction second = continuation.DecideAfterFreshDrain(0, StagnantStop, out int secondStagnant);

            Assert.Equal(HistoryCatchUpDrainAction.RetryEmpty, first);
            Assert.Equal(1, firstStagnant);
            Assert.Equal(HistoryCatchUpDrainAction.Finish, second);
            Assert.Equal(2, secondStagnant);
        }

        [Fact]
        public void Empty_drains_and_quiet_lots_share_one_stagnant_count()
        {
            var continuation = new HistoryCatchUpContinuation();
            continuation.DecideAfterFreshDrain(0, StagnantStop, out _);

            HistoryCatchUpContinueResult decision =
                continuation.DecideAfterQuietLot(DateTime.MinValue, 0, false, StagnantStop, MaxRounds, out _);

            Assert.Equal(HistoryCatchUpContinueAction.FinishFreshAndStagnant, decision.Action);
        }

        // --- Finish ------------------------------------------------------------

        [Fact]
        public void Finishing_reports_the_last_state_and_resets()
        {
            var continuation = new HistoryCatchUpContinuation();
            continuation.DecideAfterQuietLot(T0, 1, true, StagnantStop, MaxRounds, out _);
            continuation.DecideAfterQuietLot(T0, 0, true, 5, MaxRounds, out _);

            HistoryCatchUpProgress last = continuation.Finish();

            Assert.Equal(2, last.Round);
            Assert.Equal(1, last.StagnantCycles);
            Assert.Equal(T0, last.WatermarkUtc);
            Assert.False(continuation.IsActive(bannerActive: false));
            Assert.Equal(0, continuation.Snapshot().StagnantCycles);
            Assert.Equal(DateTime.MinValue, continuation.Snapshot().WatermarkUtc);
        }
    }
}
