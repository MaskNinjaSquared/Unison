using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.ViewModels;
using Unison.Uwp.Client;
using Unison.Uwp.Helpers;
using Unison.Uwp.Services;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using ZXing;
using ZXing.Common;

namespace Unison.Uwp.UI.Views
{
    public sealed partial class LoginView : UserControl
    {
        private bool _isQRExpired = false;
        private bool _pairingStarted;
        private bool _vmPropertyHooked;
        private bool _logLiveHooked;

        private LoginViewModel ViewModel => DataContext as LoginViewModel;

        public LoginView()
        {
            this.InitializeComponent();
            if (App.Services != null)
            {
                this.DataContext = App.Services.GetRequiredService<LoginViewModel>();
            }
            this.Loaded += LoginView_Loaded;
            this.Unloaded += LoginView_Unloaded;
        }

        private void LoginView_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var v = Windows.ApplicationModel.Package.Current.Id.Version;
                VersionText.Text = $"v{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
            }
            catch { VersionText.Text = "v?"; }

            EnsureViewModel();
        }

        private async void LoginInstructionHeader_SessionResetRequested(object sender, EventArgs e)
        {
            if (ViewModel != null)
            {
                await ViewModel.ResetSessionForDevAsync();
                return;
            }

            try
            {
                await App.GetWhatsAppService().ClearSessionAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[LoginView] Dev session reset failed: " + ex.Message);
            }
        }

        private void EnsureViewModel()
        {
            if (ViewModel == null && App.Services != null)
            {
                DataContext = App.Services.GetRequiredService<LoginViewModel>();
            }

            if (ViewModel != null && !_vmPropertyHooked)
            {
                ViewModel.PropertyChanged += ViewModel_PropertyChanged;
                _vmPropertyHooked = true;
                ApplyStatusFromViewModel();
            }
        }

        public async Task EnsurePairingStartedAsync()
        {
            if (_pairingStarted)
            {
                return;
            }

            _pairingStarted = true;
            try
            {
                await StartPairingFlowAsync();
            }
            catch
            {
                _pairingStarted = false;
                throw;
            }
        }

        public void Deactivate()
        {
            _pairingStarted = false;
            QRProgress.IsActive = false;
            try { SessionLogger.Instance.PairingTraceActive = false; } catch { }
        }

        private void LoginView_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_vmPropertyHooked && ViewModel != null)
            {
                ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
                _vmPropertyHooked = false;
            }

            UnhookLiveLog();
        }

        public async Task StartPairingFlowAsync()
        {
            _pairingStarted = true;
            EnsureViewModel();
            if (ViewModel == null)
            {
                _pairingStarted = false;
                QRStatusText.Text = "LoginViewModel indisponível (DI).";
                return;
            }

            try
            {
                QRProgress.IsActive = true;
                ReloadQRButton.Visibility = Visibility.Collapsed;
                QRCodeImage.Opacity = 0.5;
                QRStatusText.Text = ViewModel.StatusText ?? "Iniciando pareamento…";

                SessionLogger.Instance.WriteAlways("[Pairing/UI] StartPairingFlowAsync");
                await ViewModel.StartPairingFlowAsync();

                if (ViewModel.HasError)
                {
                    QRProgress.IsActive = false;
                    ReloadQRButton.Visibility = Visibility.Visible;
                    ApplyStatusFromViewModel();
                }
            }
            catch (Exception ex)
            {
                _pairingStarted = false;
                SessionLogger.Instance.WriteErrorAlways("[Pairing/UI] StartPairingFlowAsync exception", ex);
                Debug.WriteLine($"Pairing flow error: {ex}");
                QRProgress.IsActive = false;
                ReloadQRButton.Visibility = Visibility.Visible;
                QRStatusText.Text = "Falha: " + ex.GetType().Name + " — " + ex.Message;
            }
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LoginViewModel.QRData))
            {
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    if (!string.IsNullOrEmpty(ViewModel?.QRData))
                        DisplayQRCode(ViewModel.QRData);
                });
            }
            else if (e.PropertyName == nameof(LoginViewModel.StatusText) ||
                     e.PropertyName == nameof(LoginViewModel.HasError) ||
                     e.PropertyName == nameof(LoginViewModel.ErrorMessage) ||
                     e.PropertyName == nameof(LoginViewModel.IsLoading))
            {
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, ApplyStatusFromViewModel);
            }
        }

        private void ApplyStatusFromViewModel()
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
                _isQRExpired = false;
                ViewModel?.OnQRDisplayed();
                ApplyStatusFromViewModel();
            }
            catch (Exception ex)
            {
                SessionLogger.Instance.WriteErrorAlways("[Pairing/UI] DisplayQRCode failed", ex);
                Debug.WriteLine($"Error displaying QR: {ex}");
                QRProgress.IsActive = false;
                ReloadQRButton.Visibility = Visibility.Visible;
                ViewModel?.OnQRDisplayFailed(ex);
                ApplyStatusFromViewModel();
            }
        }

        private void ShowLogButton_Click(object sender, RoutedEventArgs e)
        {
            if (LogPanel.Visibility == Visibility.Visible)
            {
                UnhookLiveLog();
                LogPanel.Visibility = Visibility.Collapsed;
                ShowLogButton.Content = LocalizedStrings.Get("Login_ShowLog.Content");
                return;
            }

            try
            {
                ToggleLogButton.Content = SessionLogger.Instance.Enabled
                    ? LocalizedStrings.Get("Login_ToggleLogOff.Content")
                    : LocalizedStrings.Get("Login_ToggleLogOn.Content");
                RefreshLogText();
                HookLiveLog();
            }
            catch (Exception ex)
            {
                LogText.Text = LocalizedStrings.Format("Login_LogReadFail", ex.Message);
            }

            LogPanel.Visibility = Visibility.Visible;
            ShowLogButton.Content = LocalizedStrings.Get("Login_ShowLogHide.Content");
        }

        private void HookLiveLog()
        {
            if (_logLiveHooked)
            {
                return;
            }

            SessionLogger.Instance.OnLogUpdated += SessionLogger_OnLogUpdated;
            _logLiveHooked = true;
        }

        private void UnhookLiveLog()
        {
            if (!_logLiveHooked)
            {
                return;
            }

            SessionLogger.Instance.OnLogUpdated -= SessionLogger_OnLogUpdated;
            _logLiveHooked = false;
        }

        private void SessionLogger_OnLogUpdated(object sender, string line)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, RefreshLogText);
        }

        private void RefreshLogText()
        {
            try
            {
                var text = SessionLogger.Instance.GetLogText();
                LogText.Text = string.IsNullOrWhiteSpace(text)
                    ? LocalizedStrings.Get("Login_LogEmpty")
                    : text;
            }
            catch (Exception ex)
            {
                LogText.Text = LocalizedStrings.Format("Login_LogReadFail", ex.Message);
            }
        }

        private void ToggleLogButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var novo = !SessionLogger.Instance.Enabled;
                SessionLogger.Instance.Enabled = novo;
                ToggleLogButton.Content = novo
                    ? LocalizedStrings.Get("Login_ToggleLogOff.Content")
                    : LocalizedStrings.Get("Login_ToggleLogOn.Content");
                LogText.Text = novo
                    ? LocalizedStrings.Get("Login_LogEnabledHint")
                    : LocalizedStrings.Get("Login_LogDisabledHint");
            }
            catch (Exception ex)
            {
                LogText.Text = LocalizedStrings.Format("Login_LogToggleFail", ex.Message);
            }
        }

        private void ReloadQRButton_Click(object sender, RoutedEventArgs e)
        {
            _pairingStarted = false;
            _ = StartPairingFlowAsync();
        }

        private void GenerateQRButton_Click(object sender, RoutedEventArgs e)
        {
            _pairingStarted = false;
            _ = StartPairingFlowAsync();
        }

        private void LinkWithPhone_Tapped(object sender, TappedRoutedEventArgs e)
        {
            EnsureViewModel();
            if (ViewModel?.LinkWithPhoneCommand?.CanExecute(null) == true)
            {
                ViewModel.LinkWithPhoneCommand.Execute(null);
            }
        }
    }
}
