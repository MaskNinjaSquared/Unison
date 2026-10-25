// =============================================================================
// PeopleContactDisplayName
//
// Names written into the Windows People account. The "~" unsaved-contact marker
// is strip-only inside Unison; in People it would sort every unsaved push name
// under "~". Strip it at the People boundary as a safety net.
// =============================================================================
using System;
using Unison.Core.Helpers;

namespace Unison.WhatsAppClient.Helpers
{
    public static class PeopleContactDisplayName
    {
        /// <summary>
        /// Display name safe for Windows People FirstName (no leading unsaved "~").
        /// </summary>
        public static string ForWindowsPeople(string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName))
            {
                return displayName;
            }

            string trimmed = displayName.Trim();
            while (trimmed.StartsWith(DisplayNameResolution.UnsavedContactPrefix, StringComparison.Ordinal))
            {
                trimmed = trimmed.Substring(DisplayNameResolution.UnsavedContactPrefix.Length).TrimStart();
            }

            return trimmed;
        }
    }
}
