using System;
using System.Globalization;
using Windows.UI;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Converters
{
    /// <summary>"#AARRGGBB" / "#RRGGBB" from Core view models to a brush; transparent when invalid.</summary>
    public class HexColorToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return new SolidColorBrush(Parse(value as string));
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }

        private static Color Parse(string hex)
        {
            string digits = hex?.Trim().TrimStart('#');
            if (string.IsNullOrEmpty(digits) ||
                (digits.Length != 6 && digits.Length != 8) ||
                !uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint argb))
            {
                return Colors.Transparent;
            }

            if (digits.Length == 6)
            {
                argb |= 0xFF000000;
            }

            return Color.FromArgb(
                (byte)(argb >> 24),
                (byte)(argb >> 16),
                (byte)(argb >> 8),
                (byte)argb);
        }
    }
}
