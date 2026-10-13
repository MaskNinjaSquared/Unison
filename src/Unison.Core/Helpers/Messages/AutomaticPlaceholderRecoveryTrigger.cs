// =============================================================================
// AutomaticPlaceholderRecoveryTrigger
//
// Whether a missing-message recovery pass was kicked off by an automatic path
// (offline drain, deferred drain, decrypt failure) rather than a user action.
// The trigger string is an opaque diagnostic tag from the pump; only these
// substrings mean "keep trying in the background".
// =============================================================================
using System;

namespace Unison.Core.Helpers
{
    public static class AutomaticPlaceholderRecoveryTrigger
    {
        public static bool Matches(string trigger)
        {
            if (string.IsNullOrWhiteSpace(trigger))
            {
                return false;
            }

            return trigger.IndexOf("offline-complete", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   trigger.IndexOf("deferred-drain", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   trigger.IndexOf("socket:decrypt-failed", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
