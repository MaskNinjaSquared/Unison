using System;
using System.Diagnostics;
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
            if (value is string url && !string.IsNullOrWhiteSpace(url))
            {
                try
                {
                    // Support remote + local app-data URIs.
                    if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                        url.StartsWith("ms-appx", StringComparison.OrdinalIgnoreCase) ||
                        url.StartsWith("ms-appdata", StringComparison.OrdinalIgnoreCase))
                    {
                        return new BitmapImage(new Uri(url))
                        {
                            DecodePixelWidth = 1024
                        };
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Converter] Failed to convert URL: {url}. Error: {ex.Message}");
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
