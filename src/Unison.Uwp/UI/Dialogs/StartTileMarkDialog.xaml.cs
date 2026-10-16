using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Unison.Core.Models;
using Unison.Uwp.Services;

namespace Unison.Uwp.UI.Dialogs
{
    public sealed partial class StartTileMarkDialog : ContentDialog
    {
        public StartTileMark Result { get; private set; }

        public StartTileMarkDialog()
        {
            InitializeComponent();
        }

        public bool UseTransparentPlate { get; private set; }

        public void Bind(StartTileMark current, bool transparent)
        {
            Result = current;
            UseTransparentPlate = transparent;
            TransparentTileCheck.IsChecked = transparent;
            RadioButton option = OptionFor(current);
            option.IsChecked = true;
            ShowPreview(current);
        }

        private void OnMarkChecked(object sender, RoutedEventArgs e)
        {
            ShowPreview(ReadSelected());
        }

        private void OnTransparentChanged(object sender, RoutedEventArgs e)
        {
            UseTransparentPlate = TransparentTileCheck.IsChecked == true;
            ApplyPreviewPlate();
        }

        private void OnPinClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            Result = ReadSelected();
        }

        private StartTileMark ReadSelected()
        {
            if (WhatsAppWebOption.IsChecked == true)
            {
                return StartTileMark.WhatsAppWeb;
            }

            if (WhatsAppLegacyOption.IsChecked == true)
            {
                return StartTileMark.WhatsAppLegacy;
            }

            return StartTileMark.Unison;
        }

        private RadioButton OptionFor(StartTileMark mark)
        {
            if (mark == StartTileMark.WhatsAppWeb)
            {
                return WhatsAppWebOption;
            }

            if (mark == StartTileMark.WhatsAppLegacy)
            {
                return WhatsAppLegacyOption;
            }

            return UnisonOption;
        }

        private void ShowPreview(StartTileMark mark)
        {
            PreviewIcon.Source = new BitmapImage(StartTileMarkAssets.Logo(mark));
            ApplyPreviewPlate();
        }

        private void ApplyPreviewPlate()
        {
            if (PreviewPlate == null)
            {
                return;
            }

            // Keep the accent plate visible for all three marks. Transparent only softens it
            // (matches the real secondary tile background alpha) instead of erasing the preview.
            var accentBrush = Application.Current.Resources["SystemControlBackgroundAccentBrush"]
                as Windows.UI.Xaml.Media.SolidColorBrush;
            Windows.UI.Color color = accentBrush != null
                ? accentBrush.Color
                : Windows.UI.Color.FromArgb(0xFF, 0x00, 0x7A, 0x5E);
            if (TransparentTileCheck != null && TransparentTileCheck.IsChecked == true)
            {
                color.A = 0x73; // ~45% opacity plate; glyph stays full strength
            }

            PreviewPlate.Background = new Windows.UI.Xaml.Media.SolidColorBrush(color);
            PreviewPlate.Opacity = 1;
        }
    }
}
