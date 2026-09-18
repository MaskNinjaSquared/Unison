// =============================================================================
// Tests for HistoryCatchUpContinueDecision — mirrors DecideCatchUpAfterIdleBatch.
// =============================================================================
using System;
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class HistoryCatchUpContinueDecisionTests
    {
        private static readonly DateTime Noon =
            new DateTime(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Tip_advance_resets_stagnant_and_continues()
        {
            HistoryCatchUpContinueResult r = HistoryCatchUpContinueDecision.Decide(
                newestUtc: Noon.AddHours(1),
                watermarkUtc: Noon,
                progressSignals: 0,
                stillStale: true,
                stagnantCycles: 2,
                stagnantStop: 2,
                continueRound: 0,
                maxRounds: 60);

            Assert.Equal(HistoryCatchUpContinueAction.Continue, r.Action);
            Assert.True(r.TipAdvanced);
            Assert.True(r.MadeProgress);
            Assert.Equal(0, r.NextStagnantCycles);
            Assert.Equal(1, r.NextContinueRound);
            Assert.Equal(Noon.AddHours(1), r.NextWatermarkUtc);
        }

        [Fact]
        public void Chunk_signals_alone_count_as_progress_without_moving_the_tip()
        {
            HistoryCatchUpContinueResult r = HistoryCatchUpContinueDecision.Decide(
                newestUtc: Noon,
                watermarkUtc: Noon,
                progressSignals: 3,
                stillStale: true,
                stagnantCycles: 1,
                stagnantStop: 2,
                continueRound: 4,
                maxRounds: 60);

            Assert.Equal(HistoryCatchUpContinueAction.Continue, r.Action);
            Assert.False(r.TipAdvanced);
            Assert.True(r.MadeProgress);
            Assert.Equal(0, r.NextStagnantCycles);
            Assert.Equal(5, r.NextContinueRound);
            Assert.Equal(Noon, r.NextWatermarkUtc);
        }

        [Fact]
        public void Fresh_and_stagnant_stops_insisting()
        {
            HistoryCatchUpContinueResult r = HistoryCatchUpContinueDecision.Decide(
                newestUtc: Noon,
                watermarkUtc: Noon,
                progressSignals: 0,
                stillStale: false,
                stagnantCycles: 1,
                stagnantStop: 2,
                continueRound: 3,
                maxRounds: 60);

            Assert.Equal(HistoryCatchUpContinueAction.FinishFreshAndStagnant, r.Action);
            Assert.Equal(2, r.NextStagnantCycles);
            Assert.Equal(3, r.NextContinueRound);
        }

        [Fact]
        public void Stagnant_while_still_stale_also_stops()
        {
            HistoryCatchUpContinueResult r = HistoryCatchUpContinueDecision.Decide(
                newestUtc: Noon,
                watermarkUtc: Noon,
                progressSignals: 0,
                stillStale: true,
                stagnantCycles: 1,
                stagnantStop: 2,
                continueRound: 3,
                maxRounds: 60);

            Assert.Equal(HistoryCatchUpContinueAction.FinishStagnantProgress, r.Action);
            Assert.Equal(2, r.NextStagnantCycles);
        }

        [Fact]
        public void Max_rounds_stops_even_with_progress()
        {
            HistoryCatchUpContinueResult r = HistoryCatchUpContinueDecision.Decide(
                newestUtc: Noon.AddMinutes(5),
                watermarkUtc: Noon,
                progressSignals: 1,
                stillStale: true,
                stagnantCycles: 0,
                stagnantStop: 2,
                continueRound: 60,
                maxRounds: 60);

            Assert.Equal(HistoryCatchUpContinueAction.FinishMaxContinues, r.Action);
            Assert.True(r.MadeProgress);
            Assert.Equal(0, r.NextStagnantCycles);
            Assert.Equal(60, r.NextContinueRound);
        }

        [Fact]
        public void One_stagnant_lot_while_stale_still_continues()
        {
            HistoryCatchUpContinueResult r = HistoryCatchUpContinueDecision.Decide(
                newestUtc: Noon,
                watermarkUtc: Noon,
                progressSignals: 0,
                stillStale: true,
                stagnantCycles: 0,
                stagnantStop: 2,
                continueRound: 1,
                maxRounds: 60);

            Assert.Equal(HistoryCatchUpContinueAction.Continue, r.Action);
            Assert.Equal(1, r.NextStagnantCycles);
            Assert.Equal(2, r.NextContinueRound);
        }

        [Fact]
        public void Empty_store_with_signals_still_continues()
        {
            HistoryCatchUpContinueResult r = HistoryCatchUpContinueDecision.Decide(
                newestUtc: DateTime.MinValue,
                watermarkUtc: DateTime.MinValue,
                progressSignals: 2,
                stillStale: true,
                stagnantCycles: 0,
                stagnantStop: 2,
                continueRound: 0,
                maxRounds: 60);

            Assert.Equal(HistoryCatchUpContinueAction.Continue, r.Action);
            Assert.False(r.TipAdvanced);
            Assert.True(r.MadeProgress);
            Assert.Equal(DateTime.MinValue, r.NextWatermarkUtc);
        }
    }
}
