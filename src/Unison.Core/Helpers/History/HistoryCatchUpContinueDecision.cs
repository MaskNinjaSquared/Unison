// =============================================================================
// HistoryCatchUpContinueDecision
//
// After a FULL_HISTORY lot goes quiet: finish catch-up, or solicit the next page.
//
// Tip age alone must not stop pagination — older conversations arrive without
// moving the newest tip. Progress is tip advance *or* chunk signals; finish only
// after several stagnant lots (or max rounds). Decision only; socket / banner /
// watermark fields stay with the caller.
// =============================================================================
using System;

namespace Unison.Core.Helpers
{
    public enum HistoryCatchUpContinueAction
    {
        /// <summary>Tips look fresh and the last lot(s) made no progress.</summary>
        FinishFreshAndStagnant,

        /// <summary>Still stale (or unknown) but stagnant long enough to stop insisting.</summary>
        FinishStagnantProgress,

        /// <summary>Hit the hard ceiling on adjacent FULL_HISTORY rounds.</summary>
        FinishMaxContinues,

        /// <summary>Ask for the next FULL_HISTORY page.</summary>
        Continue
    }

    public struct HistoryCatchUpContinueResult
    {
        public HistoryCatchUpContinueAction Action;

        public int NextStagnantCycles;

        /// <summary>Round after a Continue (unchanged when finishing).</summary>
        public int NextContinueRound;

        public DateTime NextWatermarkUtc;

        public bool MadeProgress;

        public bool TipAdvanced;
    }

    public static class HistoryCatchUpContinueDecision
    {
        /// <param name="newestUtc">Newest stored message tip (MinValue when empty).</param>
        /// <param name="watermarkUtc">Tip watermark from the previous continue decision.</param>
        /// <param name="progressSignals">Chunk/offline signals on the quiet lot (&gt;0 counts as progress).</param>
        /// <param name="stillStale">Whether history freshness still looks owed.</param>
        /// <param name="stagnantCycles">Stagnant lots so far (before this decision).</param>
        /// <param name="stagnantStop">Lots without progress before finish.</param>
        /// <param name="continueRound">Adjacent pages already requested.</param>
        /// <param name="maxRounds">Hard ceiling on adjacent pages.</param>
        public static HistoryCatchUpContinueResult Decide(
            DateTime newestUtc,
            DateTime watermarkUtc,
            int progressSignals,
            bool stillStale,
            int stagnantCycles,
            int stagnantStop,
            int continueRound,
            int maxRounds)
        {
            bool tipAdvanced = newestUtc != DateTime.MinValue && newestUtc > watermarkUtc;
            bool madeProgress = tipAdvanced || progressSignals > 0;

            int nextStagnant = madeProgress ? 0 : stagnantCycles + 1;
            DateTime nextWatermark = watermarkUtc;
            if (madeProgress && tipAdvanced)
            {
                nextWatermark = newestUtc;
            }

            if (!stillStale && nextStagnant >= stagnantStop)
            {
                return Finish(
                    HistoryCatchUpContinueAction.FinishFreshAndStagnant,
                    nextStagnant,
                    continueRound,
                    nextWatermark,
                    madeProgress,
                    tipAdvanced);
            }

            if (nextStagnant >= stagnantStop)
            {
                return Finish(
                    HistoryCatchUpContinueAction.FinishStagnantProgress,
                    nextStagnant,
                    continueRound,
                    nextWatermark,
                    madeProgress,
                    tipAdvanced);
            }

            if (continueRound >= maxRounds)
            {
                return Finish(
                    HistoryCatchUpContinueAction.FinishMaxContinues,
                    nextStagnant,
                    continueRound,
                    nextWatermark,
                    madeProgress,
                    tipAdvanced);
            }

            if (newestUtc != DateTime.MinValue && newestUtc > nextWatermark)
            {
                nextWatermark = newestUtc;
            }
            else if (nextWatermark == DateTime.MinValue && newestUtc != DateTime.MinValue)
            {
                nextWatermark = newestUtc;
            }

            return new HistoryCatchUpContinueResult
            {
                Action = HistoryCatchUpContinueAction.Continue,
                NextStagnantCycles = nextStagnant,
                NextContinueRound = continueRound + 1,
                NextWatermarkUtc = nextWatermark,
                MadeProgress = madeProgress,
                TipAdvanced = tipAdvanced
            };
        }

        private static HistoryCatchUpContinueResult Finish(
            HistoryCatchUpContinueAction action,
            int nextStagnant,
            int continueRound,
            DateTime nextWatermark,
            bool madeProgress,
            bool tipAdvanced)
        {
            return new HistoryCatchUpContinueResult
            {
                Action = action,
                NextStagnantCycles = nextStagnant,
                NextContinueRound = continueRound,
                NextWatermarkUtc = nextWatermark,
                MadeProgress = madeProgress,
                TipAdvanced = tipAdvanced
            };
        }
    }
}
