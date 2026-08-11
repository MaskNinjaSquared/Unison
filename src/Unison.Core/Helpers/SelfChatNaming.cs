using System;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Presentation helpers for Personal (notes-to-self) chats — no ResourceLoader.
    /// UI supplies localized marker/fallback; Name on the model stays marker-free.
    /// </summary>
    public static class SelfChatNaming
    {
        public static readonly string[] KnownMarkers =
        {
            "(You)",
            "(Você)",
            "(Anda)",
            "(Tu)",
            "(Tú)",
            "(Ty)",
            "(U)"
        };

        public static readonly string[] KnownFallbacks =
        {
            "You",
            "Você",
            "Anda",
            "Tu",
            "Tú",
            "Ty",
            "U"
        };

        /// <summary>
        /// List/header label. Non-personal returns <paramref name="baseName"/> as-is.
        /// </summary>
        public static string FormatDisplayName(
            string baseName,
            bool isPersonal,
            string selfMarker,
            string selfFallback)
        {
            if (!isPersonal)
            {
                return baseName ?? string.Empty;
            }

            string clean = StripMarker(baseName);
            string marker = string.IsNullOrWhiteSpace(selfMarker) ? "(You)" : selfMarker.Trim();
            string fallback = string.IsNullOrWhiteSpace(selfFallback) ? "You" : selfFallback.Trim();

            if (string.IsNullOrWhiteSpace(clean))
            {
                return fallback;
            }

            return clean + " " + marker;
        }

        public static bool IsMarkerOnlyLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return false;
            }

            string trimmed = label.Trim();
            foreach (var fb in KnownFallbacks)
            {
                if (trimmed.Equals(fb, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool EndsWithKnownMarker(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return false;
            }

            string trimmed = label.Trim();
            foreach (var marker in KnownMarkers)
            {
                if (trimmed.EndsWith(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>True when the whole label is only a self marker / fallback word.</summary>
        public static bool IsSelfMarkerLabel(string label)
        {
            return IsMarkerOnlyLabel(label) || EndsWithKnownMarker(label);
        }

        /// <summary>Removes trailing (You)/(Você)/…; pure fallback words become null.</summary>
        public static string StripMarker(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return null;
            }

            string trimmed = label.Trim();
            if (trimmed.StartsWith("~", StringComparison.Ordinal))
            {
                trimmed = trimmed.Substring(1).Trim();
            }

            if (IsMarkerOnlyLabel(trimmed))
            {
                return null;
            }

            foreach (var marker in KnownMarkers)
            {
                if (trimmed.EndsWith(marker, StringComparison.OrdinalIgnoreCase))
                {
                    trimmed = trimmed.Substring(0, trimmed.Length - marker.Length).Trim();
                    break;
                }
            }

            return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
        }
    }
}
