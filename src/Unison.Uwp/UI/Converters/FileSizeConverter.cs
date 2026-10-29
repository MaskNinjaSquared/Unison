using System;
using Windows.UI.Xaml.Data;

namespace Unison.Uwp.UI.Converters
{
    /// <summary>
    /// Protocol <c>fileLength</c> (bytes) as KB / MB / GB for the bubble footer.
    /// Empty when the message carried no size.
    /// </summary>
    public sealed class FileSizeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return _bytes.Convert(value, targetType, parameter, language);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }

        private readonly ByteSizeConverter _bytes = new ByteSizeConverter();
    }
}
