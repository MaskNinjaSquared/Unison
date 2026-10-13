// =============================================================================
// PlaceholderResendPermit
//
// Whether this missing message may be asked of the phone again right now.
// Two tries, not while one is in flight or already on the timer, and not again
// until the previous answer has had its wait.
// =============================================================================
using System;
using Unison.Core.State;

namespace Unison.Core.Helpers
{
    public static class PlaceholderResendPermit
    {
        public static bool MaySchedule(MissingMessageCandidate candidate, DateTime nowUtc, TimeSpan responseTimeout)
        {
            if (candidate == null)
            {
                return false;
            }

            if (candidate.PlaceholderRequestCount >= 2 ||
                candidate.PlaceholderRequestInFlight ||
                candidate.PlaceholderScheduleCts != null)
            {
                return false;
            }

            return candidate.LastPlaceholderRequestUtc == DateTime.MinValue ||
                   nowUtc - candidate.LastPlaceholderRequestUtc >= responseTimeout;
        }
    }
}
