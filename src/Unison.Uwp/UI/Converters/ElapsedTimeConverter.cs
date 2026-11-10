using System;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Uwp.Services;
using Windows.UI.Xaml.Data;

namespace Unison.Uwp.UI.Converters
{
    /// <summary>
    /// Formats a UTC <see cref="DateTime"/> / <see cref="DateTime"/>? as "x minutes ago" / "x hours ago".
    /// </summary>
    public sealed class ElapsedTimeConverter : IValueConverter
    {
        private static readonly IStringResources Strings = new StringResourcesService();

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            DateTime? timestamp = value is DateTime dt ? dt : (DateTime?)null;
            return ElapsedTimeFormatter.Format(timestamp, DateTime.UtcNow, Strings);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
