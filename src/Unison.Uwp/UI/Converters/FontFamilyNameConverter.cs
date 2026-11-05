using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Converters
{
    /// <summary>
    /// Font family name from Core view models to a <see cref="FontFamily"/>. With
    /// <c>ConverterParameter=Emoji</c> the packaged WhatsApp emoji face comes first (same idea
    /// as <c>ComposerEmojiFont</c>): it maps no ASCII, so the text falls through to the named font.
    /// </summary>
    public class FontFamilyNameConverter : IValueConverter
    {
        private const string Fallback = "Segoe UI";
        private const string EmojiParameter = "Emoji";

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            string name = value as string;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = Fallback;
            }

            if (string.Equals(parameter as string, EmojiParameter, StringComparison.OrdinalIgnoreCase))
            {
                string emojiFace = ResolveEmojiFace();
                if (!string.IsNullOrEmpty(emojiFace))
                {
                    return new FontFamily(emojiFace + ", " + name);
                }
            }

            return new FontFamily(name);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }

        private static string ResolveEmojiFace()
        {
            object resource;
            if (!Application.Current.Resources.TryGetValue("ComposerEmojiFont", out resource))
            {
                return null;
            }

            string source = (resource as FontFamily)?.Source;
            if (string.IsNullOrEmpty(source))
            {
                return null;
            }

            int comma = source.IndexOf(',');
            return comma > 0 ? source.Substring(0, comma).Trim() : source.Trim();
        }
    }
}
