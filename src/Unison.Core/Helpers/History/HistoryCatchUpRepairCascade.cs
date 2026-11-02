// =============================================================================
// HistoryCatchUpRepairCascade
//
// Whether FinishStagnant / FinishMaxContinues may latch force-repair and start
// hydration-then-full. Empty-ack quiet lots already proved the phone sent no
// history payload — cascading only reopens the same ghost banner on Desktop High.
// =============================================================================
namespace Unison.Core.Helpers
{
    public static class HistoryCatchUpRepairCascade
    {
        /// <summary>
        /// True when a stagnant/max-continues finish should solicit hydration → FULL_HISTORY.
        /// </summary>
        public static bool ShouldStartAfterFinish(
            bool stillStale,
            bool allowCascade,
            bool ackAccepted,
            int progressSignals)
        {
            if (!stillStale || !allowCascade)
            {
                return false;
            }

            // WA accepted FULL_HISTORY and delivered no chunk signals: another
            // hydration-then-full round will idle the same way.
            if (ackAccepted && progressSignals <= 0)
            {
                return false;
            }

            return true;
        }
    }
}
