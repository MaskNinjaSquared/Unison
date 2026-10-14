using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Legacy info pane sits on the page black/white. Labels keep the page text color;
    /// the values use a green that stays readable on both.
    /// </summary>
    internal static class LegacyInfoText
    {
        private static readonly SolidColorBrush ValueBrush =
            new SolidColorBrush(Color.FromArgb(0xFF, 0x1D, 0xAA, 0x61));

        public static void Apply(TextBlock label, TextBlock value)
        {
            if (!ShellUi.IsWhatsAppLegacy)
            {
                return;
            }

            if (label != null)
            {
                label.Foreground = HighBrush();
            }

            if (value != null)
            {
                value.Foreground = ValueBrush;
            }
        }

        public static void ApplyValue(Control host)
        {
            if (!ShellUi.IsWhatsAppLegacy || host == null)
            {
                return;
            }

            host.Foreground = ValueBrush;
        }

        private static Brush HighBrush()
        {
            object brush;
            if (Application.Current.Resources.TryGetValue("WhatsAppTextHighBrush", out brush) && brush is Brush found)
            {
                return found;
            }

            return new SolidColorBrush(Colors.White);
        }
    }
}
