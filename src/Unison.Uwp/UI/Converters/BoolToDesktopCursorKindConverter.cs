using System;
using Unison.Uwp.UI.Helpers;
using Windows.UI.Xaml.Data;

namespace Unison.Uwp.UI.Converters
{
    /// <summary>True → hand pointer; false → default arrow (desktop only via <see cref="DesktopCursor"/>).</summary>
    public sealed class BoolToDesktopCursorKindConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return value is bool enabled && enabled
                ? DesktopCursorKind.Hand
                : DesktopCursorKind.Default;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotSupportedException();
        }
    }
}
