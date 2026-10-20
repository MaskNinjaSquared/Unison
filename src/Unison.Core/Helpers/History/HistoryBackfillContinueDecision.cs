// =============================================================================
// HistoryBackfillContinueDecision
//
// After each ON_DEMAND batch: keep looping while Pending/HasMore remain.
// All-silent batches stop the pass.
// =============================================================================

namespace Unison.Core.Helpers
{
    public enum HistoryBackfillContinueAction
    {
        Continue,
        StopDone,
        StopSilent
    }

    public static class HistoryBackfillContinueDecision
    {
        public static HistoryBackfillContinueAction Decide(
            bool hasWorkRemaining,
            bool silentBatchAbort)
        {
            if (silentBatchAbort)
            {
                return HistoryBackfillContinueAction.StopSilent;
            }

            if (!hasWorkRemaining)
            {
                return HistoryBackfillContinueAction.StopDone;
            }

            return HistoryBackfillContinueAction.Continue;
        }
    }
}
