// =============================================================================
// HistoryCatchUpContinuation
//
// Where the paginated FULL_HISTORY catch-up stands between lots: how many
// adjacent pages were asked for, how many lots in a row brought nothing, and the
// newest tip seen when the last page was asked for.
//
// Catch-up counts as active while its banner is up *or* a round is in progress;
// the banner can end between lots while the next page is still owed. Quiet lots
// and empty offline drains share one stagnant count, so either kind of silence
// moves the same counter towards the stop.
//
// Every transition happens under one lock. Sending, reconnecting, the banner and
// logging stay with the caller; this type only answers what to do next.
// =============================================================================
using System;
using Unison.Core.Helpers;

namespace Unison.Core.State
{
    public enum HistoryCatchUpDrainAction
    {
        /// <summary>The drain brought messages: ask for the next lot.</summary>
        Continue,

        /// <summary>Nothing came, but not for long enough to stop: ask once more.</summary>
        RetryEmpty,

        /// <summary>Enough empty drains in a row: the phone has nothing left.</summary>
        Finish
    }

    public struct HistoryCatchUpProgress
    {
        public int Round;

        public int StagnantCycles;

        public DateTime WatermarkUtc;
    }

    public sealed class HistoryCatchUpContinuation
    {
        private readonly object _gate = new object();
        private int _round;
        private int _stagnantCycles;
        private DateTime _watermarkUtc = DateTime.MinValue;

        public bool IsActive(bool bannerActive)
        {
            if (bannerActive)
            {
                return true;
            }

            lock (_gate)
            {
                return _round > 0;
            }
        }

        public HistoryCatchUpProgress Snapshot()
        {
            lock (_gate)
            {
                return SnapshotLocked();
            }
        }

        /// <summary>
        /// A FULL_HISTORY lot went quiet. The round only moves when the answer is to continue;
        /// a finishing answer leaves it for <see cref="Finish"/> to report.
        /// </summary>
        public HistoryCatchUpContinueResult DecideAfterQuietLot(
            DateTime newestUtc,
            int progressSignals,
            bool stillStale,
            int stagnantStop,
            int maxRounds,
            out HistoryCatchUpProgress before)
        {
            lock (_gate)
            {
                before = SnapshotLocked();
                HistoryCatchUpContinueResult decision = HistoryCatchUpContinueDecision.Decide(
                    newestUtc,
                    _watermarkUtc,
                    progressSignals,
                    stillStale,
                    _stagnantCycles,
                    stagnantStop,
                    _round,
                    maxRounds);

                _stagnantCycles = decision.NextStagnantCycles;
                _watermarkUtc = decision.NextWatermarkUtc;
                if (decision.Action == HistoryCatchUpContinueAction.Continue)
                {
                    _round = decision.NextContinueRound;
                }

                return decision;
            }
        }

        /// <summary>
        /// The offline buffer drained mid catch-up while the tip already looks fresh. An empty
        /// buffer is not proof the phone finished; only <paramref name="stagnantStop"/> of them are.
        /// </summary>
        public HistoryCatchUpDrainAction DecideAfterFreshDrain(
            int offlineCount,
            int stagnantStop,
            out int stagnantCycles)
        {
            lock (_gate)
            {
                if (offlineCount > 0)
                {
                    _stagnantCycles = 0;
                    stagnantCycles = 0;
                    return HistoryCatchUpDrainAction.Continue;
                }

                _stagnantCycles++;
                stagnantCycles = _stagnantCycles;
                return _stagnantCycles >= stagnantStop
                    ? HistoryCatchUpDrainAction.Finish
                    : HistoryCatchUpDrainAction.RetryEmpty;
            }
        }

        /// <summary>Ends the catch-up and answers with where it stood.</summary>
        public HistoryCatchUpProgress Finish()
        {
            lock (_gate)
            {
                HistoryCatchUpProgress last = SnapshotLocked();
                _round = 0;
                _stagnantCycles = 0;
                _watermarkUtc = DateTime.MinValue;
                return last;
            }
        }

        private HistoryCatchUpProgress SnapshotLocked()
        {
            return new HistoryCatchUpProgress
            {
                Round = _round,
                StagnantCycles = _stagnantCycles,
                WatermarkUtc = _watermarkUtc
            };
        }
    }
}
