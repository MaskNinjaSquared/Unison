using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Data;

namespace Unison.UWPApp.UI.Converters
{
    /// <summary>
    /// Converts a string to Visibility. Returns Visible if string is not null/empty, Collapsed otherwise.
    /// </summary>
    public class StringToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is string str && !string.IsNullOrEmpty(str))
            {
                return Visibility.Visible;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
