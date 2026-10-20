// =============================================================================
// HistoryCatchUpProgressSignal
//
// FULL_HISTORY catch-up idle lots must only treat real payload work as progress.
// ACK acceptance and banner begin extend the idle clock but must not reset the
// stagnant counter — otherwise ACK-only rounds loop forever.
// Local tip reconcile (tip-swapped) is noise: it must not count as a lot and
// must not stamp the idle clock either, or a quiet account loops forever.
// =============================================================================
using System;

namespace Unison.Core.Helpers
{
    public static class HistoryCatchUpProgressSignal
    {
        /// <summary>
        /// SQLite / list tip reconcile — not a history payload from the phone.
        /// </summary>
        public static bool IsLocalTipNoise(string reason)
        {
            return !string.IsNullOrWhiteSpace(reason) &&
                   reason.StartsWith("tip-swapped", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Whether a progress note should bump <c>ProgressSignalCount</c> for the
        /// continue/stagnant decision (tip advance is evaluated separately).
        /// </summary>
        public static bool CountsAsLotProgress(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                return false;
            }

            if (IsLocalTipNoise(reason))
            {
                return false;
            }

            if (reason.StartsWith("banner-begin", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.Equals(reason, "ack-accepted", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }
    }
}
