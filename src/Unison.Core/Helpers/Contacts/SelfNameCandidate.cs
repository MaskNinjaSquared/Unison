// =============================================================================
// SelfNameCandidate
//
// Filters one candidate for the account's own name. Dropped: blank, only the
// "(You)" marker, a masked phone, or the account's own address written back —
// formatted or not, by the same digit test contacts go through.
// An echo of the own number used to be the sidebar's fallback before any push
// name arrived, and then blocked a real name from replacing it.
// =============================================================================
using System;
using Unison.Core.Contracts;

namespace Unison.Core.Helpers
{
    public static class SelfNameCandidate
    {
        /// <returns>The candidate without its marker, or null when it is not a name.</returns>
        public static string Normalize(
            string candidate,
            string canonicalJid,
            string normalizedJid,
            ISelfMarkerNaming markers)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return null;
            }

            string trimmed = markers.StripMarker(candidate);
            if (string.IsNullOrWhiteSpace(trimmed) || markers.IsMarkerLabel(trimmed))
            {
                return null;
            }

            if (ContactLabelSanitizer.IsMaskedPhoneLabel(trimmed) ||
                IsUserPart(trimmed, canonicalJid) ||
                IsUserPart(trimmed, normalizedJid) ||
                IsPhoneEcho(trimmed, canonicalJid) ||
                IsPhoneEcho(trimmed, normalizedJid))
            {
                return null;
            }

            if (trimmed.Contains("@") &&
                string.Equals(JidHelper.Normalize(trimmed), canonicalJid, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return trimmed;
        }

        /// <summary>The label is the own number or address, not a marker and not a name.</summary>
        public static bool IsOwnPhoneEcho(string label, string meId, string meLid, ISelfMarkerNaming markers)
        {
            if (string.IsNullOrWhiteSpace(label) || (meId == null && meLid == null))
            {
                return false;
            }

            return Normalize(label, JidHelper.Normalize(meId), JidHelper.Normalize(meLid), markers) == null &&
                   !markers.IsMarkerLabel(label.Trim());
        }

        private static bool IsPhoneEcho(string label, string jid) =>
            !string.IsNullOrWhiteSpace(jid) && ContactLabelSanitizer.IsPhoneEcho(label, jid);

        private static bool IsUserPart(string label, string jid)
        {
            string user = jid?.Split('@')[0];
            return !string.IsNullOrEmpty(user) && string.Equals(label, user, StringComparison.OrdinalIgnoreCase);
        }
    }
}
