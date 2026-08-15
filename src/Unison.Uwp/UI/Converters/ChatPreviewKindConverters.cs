using System;
using Unison.Core.Models;
using Unison.Uwp.UI.Helpers;
using Windows.UI.Xaml.Data;

namespace Unison.Uwp.UI.Converters
{
    public sealed class ChatPreviewKindToLabelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is ChatPreviewKind kind)
            {
                return ChatPreviewKindPresentation.GetLabel(kind);
            }

            return string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    public sealed class ChatPreviewKindToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            ChatPreviewKind kind = value is ChatPreviewKind typed
                ? typed
                : ChatPreviewKind.Text;

            bool forIcon = parameter != null &&
                           string.Equals(parameter.ToString(), "icon", StringComparison.OrdinalIgnoreCase);

            return forIcon
                ? ChatPreviewKindPresentation.GetIconBrush(kind)
                : ChatPreviewKindPresentation.GetLabelBrush(kind);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
