// =============================================================================
// PeopleContactDisplayName
//
// Names written into the Windows People account. The "~" unsaved-contact marker
// is useful inside Unison; in People it sorts every unsaved push name under "~".
// Strip it only at the People boundary — in-app list/bubbles keep the marker.
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
