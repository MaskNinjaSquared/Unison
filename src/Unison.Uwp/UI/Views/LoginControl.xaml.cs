using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.ViewModels;
using Unison.Uwp.Client;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Animation;

namespace Unison.Uwp.UI.Views
{
    public sealed partial class LoginControl : UserControl
    {
        private const double HeaderHeight = 175;
        private const double WaveHeight = 65;

        private bool _pairingStarted;
        private bool _exitPlaying;
        private TaskCompletionSource<bool> _exitTcs;

        private LoginViewModel ViewModel => DataContext as LoginViewModel;

        public LoginControl()
        {
            this.InitializeComponent();
            if (App.Services != null)
            {
                this.DataContext = App.Services.GetRequiredService<LoginViewModel>();
            }
            this.Loaded += LoginControl_Loaded;
            this.Unloaded += LoginControl_Unloaded;
        }

        private void LoginControl_Loaded(object sender, RoutedEventArgs e)
        {
            EnsureViewModel();
            if (ViewModel == null)
            {
                return;
            }

            // Unloading detaches, and a control can be put back in the tree with the same view
            // model, so this is what makes a second visit still receive codes.
            ViewModel.Attach();

            try
            {
                var v = Windows.ApplicationModel.Package.Current.Id.Version;
                ViewModel.VersionText = $"v{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
            }
            catch
            {
                ViewModel.VersionText = "v?";
            }
        }

        /// <summary>
        /// Wave + #20C064 wipe down over the QR screen. Completes when the cover fills the page.
        /// </summary>
        public Task PlayExitTransitionAsync()
        {
            if (_exitPlaying)
            {
                return _exitTcs?.Task ?? Task.CompletedTask;
            }

            _exitPlaying = true;
            _exitTcs = new TaskCompletionSource<bool>();

            try
            {
                if (LogPanel != null)
                {
                    LogPanel.Visibility = Visibility.Collapsed;
                }

                double pageHeight = LoginRoot?.ActualHeight ?? ActualHeight;
                if (pageHeight < HeaderHeight + WaveHeight)
                {
                    pageHeight = HeaderHeight + WaveHeight + 200;
                }

                // Solid green grows with the wave; wave exits past the bottom so the
                // final frame is a full #20C064 wipe before shell navigates.
                double coverTo = pageHeight;
                double waveTravel = Math.Max(0, pageHeight - 110 + WaveHeight);

                LoginExitCover.Height = HeaderHeight;
                LoginExitWaveTranslate.Y = 0;
                LoginExitOverlay.Visibility = Visibility.Visible;

                LoginExitCoverHeightAnim.From = HeaderHeight;
                LoginExitCoverHeightAnim.To = coverTo;
                LoginExitWaveYAnim.From = 0;
                LoginExitWaveYAnim.To = waveTravel;

                EventHandler<object> onCompleted = null;
                onCompleted = (s, e) =>
                {
                    LoginExitStoryboard.Completed -= onCompleted;
                    _exitTcs.TrySetResult(true);
                };
                LoginExitStoryboard.Completed += onCompleted;
                LoginExitStoryboard.Begin();

                // Safety: never block shell navigation if Completed does not fire.
                _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () =>
                {
                    try
                    {
                        await Task.Delay(2500);
                        _exitTcs?.TrySetResult(false);
                    }
                    catch
                    {
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[LoginControl] Exit animation failed: " + ex.Message);
                LoginExitOverlay.Visibility = Visibility.Visible;
                LoginExitCover.Height = LoginRoot?.ActualHeight ?? ActualHeight;
                _exitTcs.TrySetResult(false);
            }

            return _exitTcs.Task;
        }

        private async void LoginInstructionHeader_SessionResetRequested(object sender, EventArgs e)
        {
            if (ViewModel?.DevResetSessionCommand?.CanExecute(null) == true)
            {
                ViewModel.DevResetSessionCommand.Execute(null);
                return;
            }

            if (ViewModel != null)
            {
                await ViewModel.ResetSessionForDevAsync();
            }
        }

        private void EnsureViewModel()
        {
            if (ViewModel == null && App.Services != null)
            {
                DataContext = App.Services.GetRequiredService<LoginViewModel>();
            }
        }

        public async Task EnsurePairingStartedAsync()
        {
            if (_pairingStarted)
            {
                try
                {
                    SessionLogger.Instance.WriteAlways(
                        "[Pairing/UI] EnsurePairingStartedAsync skipped (already started)");
                }
                catch { }
                return;
            }

            _pairingStarted = true;
            try
            {
                try
                {
                    SessionLogger.Instance.WriteAlways("[Pairing/UI] EnsurePairingStartedAsync → StartPairingFlow");
                }
                catch { }
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
            try
            {
                SessionLogger.Instance.WriteAlways(
                    "[Pairing/UI] LoginControl.Deactivate (wasStarted=" + _pairingStarted + ")");
            }
            catch { }
            _pairingStarted = false;
            QrCodePart?.ResetVisualState();
            ViewModel?.DeactivateDiagnostics();
            try { SessionLogger.Instance.PairingTraceActive = false; } catch { }
        }

        private void LoginControl_Unloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                LoginExitStoryboard?.Stop();
            }
            catch
            {
            }

            ViewModel?.DeactivateDiagnostics();
            ViewModel?.Detach();
        }

        public async Task StartPairingFlowAsync()
        {
            _pairingStarted = true;
            EnsureViewModel();
            if (ViewModel == null)
            {
                _pairingStarted = false;
                return;
            }

            try
            {
                QrCodePart?.BeginLoadingVisual();
                SessionLogger.Instance.WriteAlways("[Pairing/UI] StartPairingFlowAsync");
                await ViewModel.StartPairingFlowAsync();

                if (ViewModel.HasError)
                {
                    QrCodePart?.ShowReload();
                }
            }
            catch (Exception ex)
            {
                _pairingStarted = false;
                SessionLogger.Instance.WriteErrorAlways("[Pairing/UI] StartPairingFlowAsync exception", ex);
                Debug.WriteLine($"Pairing flow error: {ex}");
                QrCodePart?.ShowReload();
            }
        }

        private void QrCodePart_RefreshRequested(object sender, EventArgs e)
        {
            _pairingStarted = false;
            _ = StartPairingFlowAsync();
        }
    }
}
