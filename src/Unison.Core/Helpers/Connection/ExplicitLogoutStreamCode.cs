// =============================================================================
// ExplicitLogoutStreamCode
//
// Stream/close codes that mean the companion was deliberately logged out (or
// removed), not a transient transport blip. Disconnect policy treats these as
// "clear session / show re-link" rather than soft-reconnect.
// =============================================================================
using System;

namespace Unison.Core.Helpers
{
    public static class ExplicitLogoutStreamCode
    {
        public static bool Matches(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return false;
            }

            string trimmed = code.Trim();
            return string.Equals(trimmed, "401", StringComparison.Ordinal) ||
                   string.Equals(trimmed, "403", StringComparison.Ordinal) ||
                   string.Equals(trimmed, "device_removed", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(trimmed, "device-removed", StringComparison.OrdinalIgnoreCase);
        }
    }
}
