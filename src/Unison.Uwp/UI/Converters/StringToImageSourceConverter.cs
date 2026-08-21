using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Media.Imaging;

namespace Unison.Uwp.UI.Converters
{
    /// <summary>
    /// Converts a URL string to a BitmapImage. Returns null for null/empty strings,
    /// which Image controls handle gracefully (showing nothing).
    /// Caches by URL + decode width so recycle / dual poster bindings do not re-decode.
    /// </summary>
    public class StringToImageSourceConverter : IValueConverter
    {
        private const int MaxCacheEntries = 96;
        private static readonly ConcurrentDictionary<string, BitmapImage> Cache =
            new ConcurrentDictionary<string, BitmapImage>(StringComparer.OrdinalIgnoreCase);

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
                        // DecodePixelWidth define o tamanho em que a imagem e
                        // DECODIFICADA na memoria. Com 1024 fixo, cada avatar de 48px
                        // na tela ocupava ~3 MB de RAM -- multiplicado por dezenas de
                        // conversas, isso sozinho estourava o limite do aparelho.
                        // Agora o tamanho vem por ConverterParameter (padrao 400).
                        int decodeWidth = 400;
                        if (parameter != null)
                        {
                            int parsed;
                            if (int.TryParse(parameter.ToString(), out parsed) && parsed > 0)
                            {
                                decodeWidth = parsed;
                            }
                        }

                        string cacheKey = url + "|" + decodeWidth.ToString();
                        BitmapImage cached;
                        if (Cache.TryGetValue(cacheKey, out cached))
                        {
                            return cached;
                        }

                        // A ordem importa: o construtor que recebe a Uri ja comeca a
                        // decodificar, entao um DecodePixelWidth atribuido depois -- como
                        // fazia o inicializador de objeto -- chega tarde demais e o limite
                        // acima nao valia nada. UriSource fica sempre por ultimo.
                        var bitmap = new BitmapImage
                        {
                            // Descarta os bytes originais apos decodificar.
                            DecodePixelType = DecodePixelType.Logical,
                            DecodePixelWidth = decodeWidth
                        };
                        bitmap.UriSource = new Uri(url);

                        if (Cache.Count >= MaxCacheEntries)
                        {
                            Cache.Clear();
                        }

                        Cache[cacheKey] = bitmap;
                        return bitmap;
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
