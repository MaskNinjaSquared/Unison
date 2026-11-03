using System;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Converters
{
    /// <summary>Font family name from Core view models to a <see cref="FontFamily"/>.</summary>
    public class FontFamilyNameConverter : IValueConverter
    {
        private const string Fallback = "Segoe UI";

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            string name = value as string;
            return new FontFamily(string.IsNullOrWhiteSpace(name) ? Fallback : name);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
