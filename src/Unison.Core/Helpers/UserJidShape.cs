// =============================================================================
// UserJidShape
//
// Whether a string looks like a direct-user WhatsApp address (phone, LID, or
// hosted) rather than a group, broadcast, or newsletter. Used when scanning
// MessageKey bytes for unknown participantAlt / remoteJidAlt fields.
// =============================================================================
using System;

namespace Unison.Core.Helpers
{
    public static class UserJidShape
    {
        public static bool Matches(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string trimmed = value.Trim();
            if (trimmed.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase) ||
                trimmed.EndsWith("@broadcast", StringComparison.OrdinalIgnoreCase) ||
                trimmed.EndsWith("@newsletter", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return trimmed.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.EndsWith("@lid", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.EndsWith("@hosted", StringComparison.OrdinalIgnoreCase);
        }
    }
}
