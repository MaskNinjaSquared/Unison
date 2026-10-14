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

            if (TransparentTileCheck != null && TransparentTileCheck.IsChecked == true)
            {
                PreviewPlate.Background = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.Transparent);
                return;
            }

            PreviewPlate.Background = (Windows.UI.Xaml.Media.Brush)Application.Current.Resources["SystemControlBackgroundAccentBrush"];
        }
    }
}
