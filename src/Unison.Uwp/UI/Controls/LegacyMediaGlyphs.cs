using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Maps Segoe MDL2 media glyphs used in shared message templates onto
    /// <c>WhatsAppLegacyIcons</c> Private Use codepoints when the Legacy shell is active.
    /// </summary>
    internal static class LegacyMediaGlyphs
    {
        public const string Play = "\uEB09";
        public const string Pause = "\uEB0A";
        public const string Download = "\uEB0B";
        public const string Stop = "\uEB0C";

        public static FontFamily IconFont
        {
            get
            {
                object family;
                return Application.Current.Resources.TryGetValue("LegacyIconFont", out family)
                    ? family as FontFamily
                    : null;
            }
        }

        /// <summary>Legacy Private Use glyph, or the original when it is not a known media icon.</summary>
        public static string Resolve(string segoeOrLegacy)
        {
            if (string.IsNullOrEmpty(segoeOrLegacy))
            {
                return Download;
            }

            switch (segoeOrLegacy)
            {
                case "\uE768": // Play
                    return Play;
                case "\uE769": // Pause
                    return Pause;
                case "\uE896": // Download
                    return Download;
                case "\uE71A": // Stop
                    return Stop;
                default:
                    return segoeOrLegacy;
            }
        }
    }
}
