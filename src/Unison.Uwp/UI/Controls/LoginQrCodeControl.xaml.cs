using System;
using System.ComponentModel;
using System.Diagnostics;
using Unison.Core.ViewModels;
using Unison.Uwp.Client;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using ZXing;
using ZXing.Common;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Pairing QR card: ZXing render, reload, and tap-to-fullscreen (via VM command).
    /// Expects <see cref="LoginViewModel"/> as DataContext.
    /// </summary>
    public sealed partial class LoginQrCodeControl : UserControl
    {
        private LoginViewModel _hookedVm;

        public LoginQrCodeControl()
        {
            this.InitializeComponent();
            this.DataContextChanged += LoginQrCodeControl_DataContextChanged;
            this.Loaded += LoginQrCodeControl_Loaded;
            this.Unloaded += LoginQrCodeControl_Unloaded;
        }

        private LoginViewModel ViewModel => DataContext as LoginViewModel;

        /// <summary>Host asks to restart pairing (reload / first start).</summary>
        public event EventHandler RefreshRequested;

        public void ResetVisualState()
        {
            QRProgress.IsActive = false;
        }

        private void LoginQrCodeControl_Loaded(object sender, RoutedEventArgs e)
        {
            HookViewModel();
            ApplyFromViewModel();
        }

        private void LoginQrCodeControl_Unloaded(object sender, RoutedEventArgs e)
        {
            UnhookViewModel();
        }

        private void LoginQrCodeControl_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            UnhookViewModel();
            HookViewModel();
            ApplyFromViewModel();
        }

        private void HookViewModel()
        {
            if (_hookedVm != null || ViewModel == null)
            {
                return;
            }

            _hookedVm = ViewModel;
            _hookedVm.PropertyChanged += ViewModel_PropertyChanged;
        }

        private void UnhookViewModel()
        {
            if (_hookedVm == null)
            {
                return;
            }

            _hookedVm.PropertyChanged -= ViewModel_PropertyChanged;
            _hookedVm = null;
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (e.PropertyName == nameof(LoginViewModel.QRData))
                {
                    if (!string.IsNullOrEmpty(ViewModel?.QRData))
                    {
                        DisplayQRCode(ViewModel.QRData);
                    }
                }
                else if (e.PropertyName == nameof(LoginViewModel.StatusText) ||
                         e.PropertyName == nameof(LoginViewModel.HasError) ||
                         e.PropertyName == nameof(LoginViewModel.ErrorMessage) ||
                         e.PropertyName == nameof(LoginViewModel.IsLoading))
                {
                    ApplyFromViewModel();
                }
            });
        }

        private void ApplyFromViewModel()
        {
            if (ViewModel == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(ViewModel.StatusText))
            {
                QRStatusText.Text = ViewModel.StatusText;
            }

            if (ViewModel.IsLoading)
            {
                QRProgress.IsActive = true;
                QRCodeImage.Opacity = 0.5;
                ReloadQRButton.Visibility = Visibility.Collapsed;
            }
            else if (!string.IsNullOrEmpty(ViewModel.QRData) && !ViewModel.HasError)
            {
                QRProgress.IsActive = false;
            }

            if (ViewModel.HasError)
            {
                QRProgress.IsActive = false;
                ReloadQRButton.Visibility = Visibility.Visible;
            }
        }

        public void BeginLoadingVisual()
        {
            QRProgress.IsActive = true;
            ReloadQRButton.Visibility = Visibility.Collapsed;
            QRCodeImage.Opacity = 0.5;
        }

        public void ShowReload()
        {
            QRProgress.IsActive = false;
            ReloadQRButton.Visibility = Visibility.Visible;
        }

        private void DisplayQRCode(string qrData)
        {
            try
            {
                SessionLogger.Instance.WriteAlways(
                    "[Pairing/UI] DisplayQRCode begin len=" + (qrData?.Length ?? 0));

                var writer = new BarcodeWriter
                {
                    Format = BarcodeFormat.QR_CODE,
                    Options = new EncodingOptions
                    {
                        Height = 512,
                        Width = 512,
                        Margin = 1
                    }
                };

                var bitmap = writer.Write(qrData);
                if (bitmap == null)
                {
                    throw new InvalidOperationException("ZXing.Write retornou null");
                }

                QRCodeImage.Source = bitmap;
                QRCodeImage.Opacity = 1.0;
                QRProgress.IsActive = false;
                ReloadQRButton.Visibility = Visibility.Collapsed;
                ViewModel?.OnQRDisplayed();
                ApplyFromViewModel();
            }
            catch (Exception ex)
            {
                SessionLogger.Instance.WriteErrorAlways("[Pairing/UI] DisplayQRCode failed", ex);
                Debug.WriteLine("Error displaying QR: " + ex);
                QRProgress.IsActive = false;
                ReloadQRButton.Visibility = Visibility.Visible;
                ViewModel?.OnQRDisplayFailed(ex);
                ApplyFromViewModel();
            }
        }

        private void QRCodeImage_Tapped(object sender, TappedRoutedEventArgs e)
        {
            // Reload button (when visible) sits above the image and consumes the hit.
            if (ViewModel?.ShowQrFullscreenCommand?.CanExecute(null) == true)
            {
                ViewModel.ShowQrFullscreenCommand.Execute(null);
            }
        }

        private void ReloadQRButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
