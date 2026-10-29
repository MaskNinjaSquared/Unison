using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Converters
{
    /// <summary>
    /// Maps <see cref="Unison.Core.Models.EmojiCategory.JumpUsesUnisonFont"/> to the
    /// matching app FontFamily resource (UnisonIcons vs Segoe Fluent Icons).
    /// </summary>
    public sealed class EmojiJumpFontConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            bool useUnison = value is bool b && b;
            string key = useUnison ? "UnisonIconFont" : "IconFont";
            if (Application.Current?.Resources != null &&
                Application.Current.Resources.TryGetValue(key, out object resource) &&
                resource is FontFamily family)
            {
                return family;
            }

            return FontFamily.XamlAutoFontFamily;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
