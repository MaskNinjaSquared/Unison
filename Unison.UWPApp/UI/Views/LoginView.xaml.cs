using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.UWPApp.Client;
using Unison.UWPApp.Services;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media.Imaging;
using ZXing;
using ZXing.Common;

namespace Unison.UWPApp.UI.Views
{
    public sealed partial class LoginView : UserControl
    {
        private PairingHandler _pairingHandler;
        private bool _isQRExpired = false;

        public LoginView()
        {
            this.InitializeComponent();
            this.Loaded += LoginView_Loaded;
        }

        private void LoginView_Loaded(object sender, RoutedEventArgs e)
        {
            _ = StartPairingFlowAsync();
        }

        public async Task StartPairingFlowAsync()
        {
            try
            {
                QRProgress.IsActive = true;
                ReloadQRButton.Visibility = Visibility.Collapsed;
                QRCodeImage.Opacity = 0.5;

                // Listen for QR code from socket BEFORE connecting
                var socket = WhatsAppService.Instance.Socket;
                if (socket != null)
                {
                    socket.OnQRCodeReceived -= Socket_OnQRCodeReceived;
                }

                await WhatsAppService.Instance.ConnectAsync();
                
                socket = WhatsAppService.Instance.Socket;
                if (socket != null)
                {
                    socket.OnQRCodeReceived += Socket_OnQRCodeReceived;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Pairing flow error: {ex.Message}");
                QRProgress.IsActive = false;
                ReloadQRButton.Visibility = Visibility.Visible;
            }
        }

        private void Socket_OnQRCodeReceived(object sender, string qrData)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                DisplayQRCode(qrData);
            });
        }

        private void DisplayQRCode(string qrData)
        {
            try
            {
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
                QRCodeImage.Source = bitmap;
                QRCodeImage.Opacity = 1.0;
                QRProgress.IsActive = false;
                ReloadQRButton.Visibility = Visibility.Collapsed;
                _isQRExpired = false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error displaying QR: {ex.Message}");
            }
        }

        private void ReloadQRButton_Click(object sender, RoutedEventArgs e)
        {
            _ = StartPairingFlowAsync();
        }

        private void GenerateQRButton_Click(object sender, RoutedEventArgs e)
        {
            _ = StartPairingFlowAsync();
        }

        private void LinkWithPhone_Tapped(object sender, TappedRoutedEventArgs e)
        {
            // Placeholder for phone linking
        }
    }
}
