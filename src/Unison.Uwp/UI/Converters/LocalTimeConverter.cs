using System;
using Unison.Core.Mappers;
using Windows.UI.Xaml.Data;

namespace Unison.Uwp.UI.Converters
{
    /// <summary>
    /// Formats a message stamp stored as GMT 0 (UTC) in the device time zone.
    /// ConverterParameter: empty or <c>t</c> → HH:mm, <c>d</c> → date, <c>g</c> → date+time.
    /// </summary>
    public sealed class LocalTimeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            DateTime timestamp;
            if (value is DateTime dt)
            {
                timestamp = dt;
            }
            else if (value != null && Nullable.GetUnderlyingType(value.GetType()) == typeof(DateTime))
            {
                timestamp = (DateTime)value;
            }
            else
            {
                return string.Empty;
            }

            string mode = parameter as string;
            if (string.Equals(mode, "d", StringComparison.OrdinalIgnoreCase))
            {
                return WhatsAppMapper.FormatLocalDate(timestamp);
            }

            if (string.Equals(mode, "g", StringComparison.OrdinalIgnoreCase))
            {
                return WhatsAppMapper.FormatLocalDateTime(timestamp);
            }

            return WhatsAppMapper.FormatLocalTime(timestamp);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
