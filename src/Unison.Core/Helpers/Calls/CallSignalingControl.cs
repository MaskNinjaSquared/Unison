// =============================================================================
// CallSignalingControl
//
// Call action tags that only feed the call engine (deferred accept, relay
// election, transport) — never chat chrome, CallLog or toasts.
// =============================================================================
using System;

namespace Unison.Core.Helpers.Calls
{
    public static class CallSignalingControl
    {
        public static bool IsControlTag(string actionTag)
        {
            return string.Equals(actionTag, "mute_v2", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(actionTag, "relaylatency", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(actionTag, "transport", StringComparison.OrdinalIgnoreCase);
        }
    }
}
