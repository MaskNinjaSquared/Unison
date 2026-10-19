// =============================================================================
// LegacyGroupAvatarMissReason
//
// Failure reasons from older group-avatar fetches that still mean "no picture"
// (404 / 406 / no-picture) and should not burn a fresh miss backoff forever.
// =============================================================================
using System;

namespace Unison.Core.Helpers
{
    public static class LegacyGroupAvatarMissReason
    {
        public static bool Matches(string reason)
        {
            return string.Equals(reason, "server-error:404", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(reason, "server-error:406", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(reason, "no-picture", StringComparison.OrdinalIgnoreCase);
        }
    }
}
