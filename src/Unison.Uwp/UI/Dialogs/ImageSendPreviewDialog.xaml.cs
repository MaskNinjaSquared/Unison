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

        public async System.Threading.Tasks.Task SetPreviewAsync(byte[] imageBytes, string infoText)
        {
            InfoText.Text = infoText ?? string.Empty;
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
