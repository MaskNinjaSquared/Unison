using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Data;
using Unison.Core.Models;

namespace Unison.Uwp.UI.Converters
{
    /// <summary>
    /// Visible when the bubble kind matches the converter parameter.
    /// Parameter "Audio" matches voice and audio.
    /// </summary>
    public sealed class ChatMessageKindVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            ChatMessageKind kind = value is ChatMessageKind typed ? typed : ChatMessageKind.Text;
            string expected = parameter as string;
            bool match;
            if (string.Equals(expected, "Audio", StringComparison.Ordinal))
            {
                match = kind == ChatMessageKind.Voice || kind == ChatMessageKind.Audio;
            }
            else
            {
                ChatMessageKind parsed;
                match = Enum.TryParse(expected, out parsed) && kind == parsed;
            }

            return match ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotSupportedException();
        }
    }
}
