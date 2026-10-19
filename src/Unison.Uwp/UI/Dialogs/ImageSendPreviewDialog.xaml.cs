using System;
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

        public async System.Threading.Tasks.Task SetPreviewAsync(
            byte[] imageBytes,
            string infoText,
            string initialCaption = null)
        {
            InfoText.Text = infoText ?? string.Empty;
            CaptionBox.Text = initialCaption ?? string.Empty;
            PreviewImage.Source = null;

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
    }
}
