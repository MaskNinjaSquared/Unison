using System;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Uwp.Helpers
{
    /// <summary>
    /// Thin UWP helpers for stripping legacy self markers in the connection client.
    /// UI display goes through <see cref="ChatItem.GetNameResolved"/>.
    /// </summary>
    public static class SelfChatDisplayHelper
    {
        public static bool IsSelfMarkerLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return false;
            }

            string trimmed = label.Trim();
            if (SelfChatNaming.IsMarkerOnlyLabel(trimmed) || SelfChatNaming.EndsWithKnownMarker(trimmed))
            {
                return true;
            }

            try
            {
                string currentFallback = LocalizedStrings.Get("Chat_SelfFallbackName", "You");
                if (!string.IsNullOrEmpty(currentFallback) &&
                    trimmed.Equals(currentFallback, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                string currentMarker = LocalizedStrings.Get("Chat_SelfMarker", "(You)");
                if (!string.IsNullOrEmpty(currentMarker) &&
                    trimmed.EndsWith(currentMarker, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        public static string StripSelfMarker(string label)
        {
            string stripped = SelfChatNaming.StripMarker(label);
            if (stripped != null)
            {
                return stripped;
            }

            if (string.IsNullOrWhiteSpace(label))
            {
                return null;
            }

            try
            {
                string trimmed = label.Trim();
                string currentMarker = LocalizedStrings.Get("Chat_SelfMarker", "(You)");
                if (!string.IsNullOrEmpty(currentMarker) &&
                    trimmed.EndsWith(currentMarker, StringComparison.OrdinalIgnoreCase))
                {
                    trimmed = trimmed.Substring(0, trimmed.Length - currentMarker.Length).Trim();
                    return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
                }
            }
            catch
            {
            }

            return SelfChatNaming.IsMarkerOnlyLabel(label?.Trim()) ? null : SelfChatNaming.StripMarker(label);
        }
    }
}
