using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Data;

namespace Unison.UWPApp.UI.Converters
{
    /// <summary>
    /// Inverted string to visibility - returns Collapsed if string is not null/empty, Visible otherwise.
    /// Used to hide fallback elements when a value (like AvatarUrl) is present.
    /// </summary>
    public class InvertedStringToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is string str && !string.IsNullOrEmpty(str))
            {
                return Visibility.Collapsed;
            }
            return Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
