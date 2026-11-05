// =============================================================================
// PhoneDisplayMaskConverter
//
// XAML wrapper around PhoneDisplayMask.Format ("+CC DDD LOCAL").
// =============================================================================
using System;
using Unison.Core.Helpers;
using Windows.UI.Xaml.Data;

namespace Unison.Uwp.UI.Converters
{
    public sealed class PhoneDisplayMaskConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return PhoneDisplayMask.Format(value as string);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotSupportedException();
        }
    }
}
