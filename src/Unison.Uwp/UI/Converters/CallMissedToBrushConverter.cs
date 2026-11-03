using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Converters
{
    /// <summary>Missed call → ChatDetailSendFailedBrush; otherwise WhatsAppTextMediumBrush.</summary>
    public sealed class CallMissedToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            bool missed = value is bool b && b;
            string key = missed ? "ChatDetailSendFailedBrush" : "WhatsAppTextMediumBrush";
            object brush;
            if (Application.Current.Resources.TryGetValue(key, out brush) && brush is Brush typed)
            {
                return typed;
            }

            return Application.Current.Resources["WhatsAppTextMediumBrush"];
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
