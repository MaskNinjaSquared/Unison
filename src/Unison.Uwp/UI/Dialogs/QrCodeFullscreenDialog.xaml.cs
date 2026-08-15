using System;
using Unison.Uwp.Helpers;
using Windows.UI.Xaml.Controls;
using ZXing;
using ZXing.Common;

namespace Unison.Uwp.UI.Dialogs
{
    /// <summary>
    /// Full-size QR preview for pairing (opened from the login QR tap).
    /// </summary>
    public sealed partial class QrCodeFullscreenDialog : ContentDialog
    {
        public QrCodeFullscreenDialog()
        {
            this.InitializeComponent();
            CloseButtonText = LocalizedStrings.Get("Common_OK", "OK");
        }

        public void SetQrPayload(string qrData)
        {
            if (string.IsNullOrEmpty(qrData))
            {
                QrImage.Source = null;
                return;
            }

            var writer = new BarcodeWriter
            {
                Format = BarcodeFormat.QR_CODE,
                Options = new EncodingOptions
                {
                    Height = 800,
                    Width = 800,
                    Margin = 1
                }
            };

            var bitmap = writer.Write(qrData);
            if (bitmap == null)
            {
                throw new InvalidOperationException("ZXing.Write returned null");
            }

            QrImage.Source = bitmap;
        }
    }
}
