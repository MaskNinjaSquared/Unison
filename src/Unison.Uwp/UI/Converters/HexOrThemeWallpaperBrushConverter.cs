using System;
using System.Globalization;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Converters
{
    /// <summary>
    /// Hex (#AARRGGBB / #RRGGBB) → brush. Null/empty/invalid →
    /// <c>ChatDetailWallpaperBackgroundBrush</c> (light/dark doodle underfill).
    /// </summary>
    public sealed class HexOrThemeWallpaperBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (TryParse(value as string, out Color color))
            {
                return new SolidColorBrush(color);
            }

            if (Application.Current?.Resources != null &&
                Application.Current.Resources.TryGetValue(
                    "ChatDetailWallpaperBackgroundBrush",
                    out object brushObj) &&
                brushObj is Brush themeBrush)
            {
                return themeBrush;
            }

            return new SolidColorBrush(Colors.Transparent);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }

        private static bool TryParse(string hex, out Color color)
        {
            color = Colors.Transparent;
            string digits = hex?.Trim().TrimStart('#');
            if (string.IsNullOrEmpty(digits) ||
                (digits.Length != 6 && digits.Length != 8) ||
                !uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint argb))
            {
                return false;
            }

            if (digits.Length == 6)
            {
                argb |= 0xFF000000;
            }

            color = Color.FromArgb(
                (byte)(argb >> 24),
                (byte)(argb >> 16),
                (byte)(argb >> 8),
                (byte)argb);
            return true;
        }
    }
}
