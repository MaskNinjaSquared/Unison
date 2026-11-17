using System;
using System.Globalization;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Data;

namespace Unison.Uwp.UI.Converters
{
    /// <summary>
    /// <c>true</c> (e.g. selected) → <see cref="CornerRadius"/> 0;
    /// <c>false</c> → uniform radius from ConverterParameter (default 2).
    /// </summary>
    public sealed class BoolClearsCornerRadiusConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            bool clear = value is bool flag && flag;
            if (clear)
            {
                return new CornerRadius(0);
            }

            double radius = 2;
            if (parameter != null)
            {
                string text = parameter as string ?? parameter.ToString();
                if (!string.IsNullOrWhiteSpace(text) &&
                    double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                {
                    radius = parsed;
                }
            }

            return new CornerRadius(radius);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
