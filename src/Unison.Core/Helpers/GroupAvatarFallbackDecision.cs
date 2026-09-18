// =============================================================================
// GroupAvatarFallbackDecision
//
// Whether a failed group profile-picture fetch should try sibling / parent
// group avatar fallbacks. Empty URL plus not-found or selected server errors.
// =============================================================================
using System;

namespace Unison.Core.Helpers
{
    public static class GroupAvatarFallbackDecision
    {
        public static bool ShouldTry(string url, bool isNotFound, string failureReason)
        {
            if (!string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            return isNotFound ||
                   string.Equals(failureReason, "server-error:401", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(failureReason, "server-error:404", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(failureReason, "server-error:406", StringComparison.OrdinalIgnoreCase);
        }
    }
}
