using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;

namespace Unison.Uwp.UI.Dialogs
{
    /// <summary>
    /// ContentDialog used by <see cref="Services.DialogService.ShowImageSendPreviewAsync"/>.
    /// </summary>
    public sealed partial class ImageSendPreviewDialog : ContentDialog
    {
        private IReadOnlyList<byte[]> _imageBytes;
        private int _selectedIndex;

        public ImageSendPreviewDialog()
        {
            this.InitializeComponent();
        }

        /// <summary>Caption typed by the user; trimmed, or null when empty.</summary>
        public string Caption
        {
            get
            {
                string text = CaptionBox?.Text;
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            }
        }

        public async System.Threading.Tasks.Task SetPreviewsAsync(
            IReadOnlyList<byte[]> imageBytes,
            string infoText,
            string initialCaption = null)
        {
            InfoText.Text = infoText ?? string.Empty;
            CaptionBox.Text = initialCaption ?? string.Empty;
            _imageBytes = imageBytes;
            _selectedIndex = 0;
            await ShowSelectedImageAsync();
        }

        private async System.Threading.Tasks.Task ShowSelectedImageAsync()
        {
            PreviewImage.Source = null;
            int count = _imageBytes?.Count ?? 0;
            if (count == 0)
            {
                ImageIndexText.Text = string.Empty;
                PreviousButton.Visibility = Windows.UI.Xaml.Visibility.Collapsed;
                NextButton.Visibility = Windows.UI.Xaml.Visibility.Collapsed;
                return;
            }

            _selectedIndex = Math.Max(0, Math.Min(_selectedIndex, count - 1));
            ImageIndexText.Text = count > 1 ? (_selectedIndex + 1) + " / " + count : string.Empty;
            PreviousButton.Visibility = _selectedIndex > 0
                ? Windows.UI.Xaml.Visibility.Visible
                : Windows.UI.Xaml.Visibility.Collapsed;
            NextButton.Visibility = _selectedIndex < count - 1
                ? Windows.UI.Xaml.Visibility.Visible
                : Windows.UI.Xaml.Visibility.Collapsed;

            byte[] imageBytes = _imageBytes[_selectedIndex];
            if (imageBytes == null || imageBytes.Length == 0)
            {
                return;
            }

            var bitmap = new BitmapImage { DecodePixelWidth = 480 };
            using (var memStream = new InMemoryRandomAccessStream())
            {
                await memStream.WriteAsync(imageBytes.AsBuffer());
                memStream.Seek(0);
                await bitmap.SetSourceAsync(memStream);
            }

            PreviewImage.Source = bitmap;
        }

        private async void PreviousButton_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            _selectedIndex--;
            await ShowSelectedImageAsync();
        }

        private async void NextButton_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            _selectedIndex++;
            await ShowSelectedImageAsync();
        }

        private async void PreviewHost_ManipulationCompleted(
            object sender,
            Windows.UI.Xaml.Input.ManipulationCompletedRoutedEventArgs e)
        {
            if (e.Cumulative.Translation.X <= -48 && _selectedIndex < (_imageBytes?.Count ?? 0) - 1)
            {
                _selectedIndex++;
                await ShowSelectedImageAsync();
            }
            else if (e.Cumulative.Translation.X >= 48 && _selectedIndex > 0)
            {
                _selectedIndex--;
                await ShowSelectedImageAsync();
            }
        }
    }
}
