using System;
using System.Globalization;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Data;

namespace Unison.Uwp.UI.Converters
{
    /// <summary>
    /// <c>true</c> → uniform <see cref="Thickness"/> from ConverterParameter (default 4);
    /// <c>false</c> → 0.
    /// </summary>
    public sealed class BoolToThicknessConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            bool on = value is bool flag && flag;
            if (!on)
            {
                return new Thickness(0);
            }

            double inset = 4;
            if (parameter != null)
            {
                string text = parameter as string ?? parameter.ToString();
                if (!string.IsNullOrWhiteSpace(text) &&
                    double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                {
                    inset = parsed;
                }
            }

            return new Thickness(inset);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
