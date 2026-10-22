using Unison.Core.Helpers;

// =============================================================================
// HistoryBackfillFlightClassifier
//
// Labels one ON_DEMAND settle: ACK-silent, RAM grew, chunk without growth, etc.
// =============================================================================

namespace Unison.WhatsAppClient.Helpers
{
    public struct HistoryBackfillFlightObservation
    {
        public bool HadRequest;
        public bool AckAccepted;
        public bool AckRejected;
        public bool ChunkApplied;
        public int BaselineRam;
        public int AfterRam;
    }

    public static class HistoryBackfillFlightClassifier
    {
        public static HistoryBackfillFlightOutcome Classify(HistoryBackfillFlightObservation o)
        {
            if (!o.HadRequest)
            {
                return HistoryBackfillFlightOutcome.NoRequest;
            }

            if (o.AckRejected)
            {
                return HistoryBackfillFlightOutcome.Rejected;
            }

            if (o.AfterRam > o.BaselineRam)
            {
                return HistoryBackfillFlightOutcome.RamGrew;
            }

            if (o.ChunkApplied)
            {
                return HistoryBackfillFlightOutcome.ChunkNoRamGrowth;
            }

            if (o.AckAccepted)
            {
                return HistoryBackfillFlightOutcome.AckSilent;
            }

            return HistoryBackfillFlightOutcome.NoAck;
        }
    }
}
