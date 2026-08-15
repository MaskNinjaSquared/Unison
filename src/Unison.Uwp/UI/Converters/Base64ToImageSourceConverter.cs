using System;
using Windows.Storage.Streams;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;

namespace Unison.Uwp.UI.Converters
{
    /// <summary>Decodes a JPEG/PNG base64 string to a small BitmapImage (info-grid placeholders).</summary>
    public sealed class Base64ToImageSourceConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var encoded = value as string;
            if (string.IsNullOrWhiteSpace(encoded))
            {
                return null;
            }

            try
            {
                byte[] bytes = System.Convert.FromBase64String(encoded);
                if (bytes == null || bytes.Length == 0)
                {
                    return null;
                }

                int decodeWidth = 40;
                if (parameter != null)
                {
                    int parsed;
                    if (int.TryParse(parameter.ToString(), out parsed) && parsed > 0)
                    {
                        decodeWidth = parsed;
                    }
                }

                var stream = new InMemoryRandomAccessStream();
                stream.WriteAsync(bytes.AsBuffer()).AsTask().GetAwaiter().GetResult();
                stream.Seek(0);

                var image = new BitmapImage
                {
                    DecodePixelWidth = decodeWidth,
                    DecodePixelType = DecodePixelType.Logical
                };
                image.SetSource(stream);
                return image;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
