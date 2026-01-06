using System;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Media.Imaging;

namespace Unison.UWPApp.UI.Converters
{
    /// <summary>
    /// Converts a URL string to a BitmapImage. Returns null for null/empty strings,
    /// which Image controls handle gracefully (showing nothing).
    /// </summary>
    public class StringToImageSourceConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is string url && !string.IsNullOrEmpty(url))
            {
                try
                {
                    return new BitmapImage(new Uri(url, UriKind.Absolute))
                    {
                        DecodePixelWidth = 96,
                        DecodePixelHeight = 96
                    };
                }
                catch
                {
                    // Invalid URL format
                    return null;
                }
            }
            return null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
