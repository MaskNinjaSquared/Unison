using System;
using Unison.Core.Models;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Converters
{
    /// <summary>Missed → red; Incoming/Outgoing → medium text.</summary>
    public sealed class CallDirectionToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            bool missed = value is CallDirection direction && direction == CallDirection.Missed;
            string key = missed ? "ChatDetailSendFailedBrush" : "WhatsAppTextMediumBrush";
            object brush;
            if (Application.Current?.Resources != null &&
                Application.Current.Resources.TryGetValue(key, out brush) &&
                brush is Brush)
            {
                return brush;
            }

            return missed
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xDE, 0x3B, 0x3B))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xCC, 0xCC, 0xCC));
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
