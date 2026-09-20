using System;
using Unison.Core.ViewModels;
using Unison.Uwp.Helpers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using ZXing;
using ZXing.Common;

namespace Unison.Uwp.UI.Dialogs
{
    /// <summary>
    /// Full-size QR preview for pairing (opened from the login QR tap).
    /// <see cref="DataContext"/> is the same <see cref="LoginViewModel"/> as the login surface
    /// (Imgur-style: DialogService passes the VM). Closes on Fechar or
    /// <see cref="LoginViewModel.QrFullscreenDismissRequested"/>.
    /// </summary>
    public sealed partial class QrCodeFullscreenDialog : ContentDialog
    {
        private LoginViewModel _hookedVm;

        public QrCodeFullscreenDialog()
        {
            this.InitializeComponent();
            // No system Close/Primary chrome — custom green button in content.
            PrimaryButtonText = string.Empty;
            SecondaryButtonText = string.Empty;
            CloseButtonText = string.Empty;
            CloseButton.Content = LocalizedStrings.Get("Common_Close", "Close");
            DataContextChanged += QrCodeFullscreenDialog_DataContextChanged;
            Closed += QrCodeFullscreenDialog_Closed;
        }

        private LoginViewModel ViewModel => DataContext as LoginViewModel;

        private void QrCodeFullscreenDialog_DataContextChanged(
            FrameworkElement sender,
            DataContextChangedEventArgs args)
        {
            UnhookViewModel();
            HookViewModel();
            if (ViewModel != null && !string.IsNullOrEmpty(ViewModel.QRData))
            {
                SetQrPayload(ViewModel.QRData);
            }
        }

        private void HookViewModel()
        {
            if (_hookedVm != null || ViewModel == null)
            {
                return;
            }

            _hookedVm = ViewModel;
            _hookedVm.QrFullscreenDismissRequested += LoginVm_QrFullscreenDismissRequested;
        }

        private void UnhookViewModel()
        {
            if (_hookedVm == null)
            {
                return;
            }

            _hookedVm.QrFullscreenDismissRequested -= LoginVm_QrFullscreenDismissRequested;
            _hookedVm = null;
        }

        private void LoginVm_QrFullscreenDismissRequested(object sender, EventArgs e)
        {
            try
            {
                Hide();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[QrCodeFullscreenDialog] Hide after dismiss request failed: " + ex.Message);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        private void QrCodeFullscreenDialog_Closed(ContentDialog sender, ContentDialogClosedEventArgs args)
        {
            UnhookViewModel();
            DataContext = null;
        }

        /// <summary>Debug / raw-payload path when there is no login VM.</summary>
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
