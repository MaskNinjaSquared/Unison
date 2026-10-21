// =============================================================================
// HistoryBackfillAbortDecision
//
// Companion phones often ACK every HISTORY_SYNC_ON_DEMAND and never send a chunk.
// One all-silent batch is enough to stop the hydration pass.
// =============================================================================

namespace Unison.Core.Helpers
{
    public static class HistoryBackfillAbortDecision
    {
        public static bool ShouldAbortAfterSilentBatch(
            int sent,
            int ackSilent,
            int noAck,
            int rejected,
            int ramGrew)
        {
            if (sent <= 0 || ramGrew > 0)
            {
                return false;
            }

            int unanswered = ackSilent + noAck + rejected;
            return unanswered >= sent;
        }
    }
}
