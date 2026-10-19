// =============================================================================
// HistoryCatchUpProgressSignal
//
// FULL_HISTORY catch-up idle lots must only treat real payload work as progress.
// ACK acceptance and banner begin extend the idle clock but must not reset the
// stagnant counter — otherwise ACK-only rounds loop forever.
// =============================================================================
using System;

namespace Unison.Core.Helpers
{
    public static class HistoryCatchUpProgressSignal
    {
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
