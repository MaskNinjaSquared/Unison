using System;
using Unison.Core.Mappers;
using Unison.Uwp.Helpers;
using Windows.UI.Xaml.Data;

namespace Unison.Uwp.UI.Converters
{
    /// <summary>
    /// Formats a <see cref="DateTime"/> / <see cref="DateTime"/>? for chat-list timestamps
    /// (today → time, yesterday → <c>Common_Yesterday</c>, else weekday/date).
    /// </summary>
    public sealed class RelativeTimestampConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            DateTime? timestamp = null;
            if (value is DateTime dt)
            {
                timestamp = dt;
            }
            else if (value != null)
            {
                // Nullable boxed as object
                var underlying = Nullable.GetUnderlyingType(value.GetType());
                if (underlying == typeof(DateTime))
                {
                    timestamp = (DateTime)value;
                }
            }

            string yesterday = LocalizedStrings.Get("Common_Yesterday", "Yesterday");
            return WhatsAppMapper.FormatTimestamp(timestamp, yesterday);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
