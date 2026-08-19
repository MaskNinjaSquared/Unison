using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.ViewModels;
using Unison.Uwp.Client;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;

namespace Unison.Uwp.UI.Views
{
    /// <summary>
    /// Full-page QR / pairing. Root back stack is cleared — system Back does not leave this page
    /// toward Shell or Start (handled + button hidden).
    /// </summary>
    public sealed partial class LoginView : Page
    {
        private const double HeaderHeight = 175;
        private const double WaveHeight = 65;

        private ShellViewModel _shell;
        private bool _backHooked;
        private bool _exitTransitionRunning;
        private bool _pairingStarted;
        private bool _exitPlaying;
        private TaskCompletionSource<bool> _exitTcs;

        private LoginViewModel ViewModel => DataContext as LoginViewModel;

        public LoginView()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Disabled;
            if (App.Services != null)
            {
                DataContext = App.Services.GetRequiredService<LoginViewModel>();
            }

            Loaded += LoginView_Loaded;
            Unloaded += LoginView_Unloaded;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility =
                AppViewBackButtonVisibility.Collapsed;

            if (!_backHooked)
            {
                SystemNavigationManager.GetForCurrentView().BackRequested += LoginView_BackRequested;
                _backHooked = true;
            }

            _shell = App.Services?.GetService<ShellViewModel>();
            if (_shell != null)
            {
                _shell.PropertyChanged -= Shell_PropertyChanged;
                _shell.PropertyChanged += Shell_PropertyChanged;
                _shell.LoginExitTransitionRequested -= Shell_LoginExitTransitionRequested;
                _shell.LoginExitTransitionRequested += Shell_LoginExitTransitionRequested;
            }

            PairingTrace(
                "OnNavigatedTo surface=" + (_shell?.AppSurface ?? "(null)") +
                " startPairing=" + (_shell?.StartPairingOnLoginSurface == true));
            ApplyPairingState();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            PairingTrace("OnNavigatedFrom → Deactivate");
            if (_shell != null)
            {
                _shell.PropertyChanged -= Shell_PropertyChanged;
                _shell.LoginExitTransitionRequested -= Shell_LoginExitTransitionRequested;
                _shell = null;
            }

            if (_backHooked)
            {
                SystemNavigationManager.GetForCurrentView().BackRequested -= LoginView_BackRequested;
                _backHooked = false;
            }

            Deactivate();
        }

        private void LoginView_BackRequested(object sender, BackRequestedEventArgs e)
        {
            // QR is a root terminal page: never GoBack from here.
            e.Handled = true;
        }

        private async void Shell_LoginExitTransitionRequested(object sender, EventArgs e)
        {
            if (_exitTransitionRunning)
            {
                return;
            }

            _exitTransitionRunning = true;
            ShellViewModel shell = _shell;
            try
            {
                PairingTrace("Login exit transition → PlayExitTransitionAsync");
                Deactivate();
                await PlayExitTransitionAsync();

                PairingTrace("Login exit transition done → CompleteEnterConnectedNavigation");
                shell?.CompleteEnterConnectedNavigation();
            }
            catch (Exception ex)
            {
                PairingTrace("Login exit transition FAILED: " + ex.Message);
                shell?.CompleteEnterConnectedNavigation();
            }
        }

        private void Shell_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ShellViewModel.AppSurface) ||
                e.PropertyName == nameof(ShellViewModel.StartPairingOnLoginSurface))
            {
                PairingTrace(
                    "Shell PropertyChanged " + e.PropertyName +
                    " surface=" + (_shell?.AppSurface ?? "(null)") +
                    " startPairing=" + (_shell?.StartPairingOnLoginSurface == true));
                ApplyPairingState();
            }
        }

        private void ApplyPairingState()
        {
            // Leaving Login (e.g. session-initialized → Connected) must NOT restart QR.
            // EnterConnectedSurface used to flip AppSurface while this page was still
            // subscribed; Deactivate() cleared _pairingStarted and EnsurePairingStarted
            // immediately began ConnectAsync again — stuck on QR after WhatsApp synced.
            if (_shell == null ||
                !string.Equals(_shell.AppSurface, ShellViewModel.SurfaceLogin, StringComparison.Ordinal))
            {
                PairingTrace(
                    "ApplyPairingState → Deactivate only (surface=" +
                    (_shell?.AppSurface ?? "(null)") + ")");
                Deactivate();
                return;
            }

            Deactivate();
            if (_shell.StartPairingOnLoginSurface)
            {
                PairingTrace("ApplyPairingState → EnsurePairingStartedAsync");
                _ = EnsurePairingStartedAsync();
            }
            else
            {
                PairingTrace("ApplyPairingState → wait (StartPairingOnLoginSurface=false)");
            }
        }

        private void LoginView_Loaded(object sender, RoutedEventArgs e)
        {
            EnsureViewModel();
            if (ViewModel == null)
            {
                return;
            }

            // Unloading detaches, and a page can be shown again with the same view
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
        private Task PlayExitTransitionAsync()
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
                onCompleted = (s, ev) =>
                {
                    LoginExitStoryboard.Completed -= onCompleted;
                    _exitTcs.TrySetResult(true);
                };
                LoginExitStoryboard.Completed += onCompleted;
                LoginExitStoryboard.Begin();

                // Safety: never block shell navigation if Completed does not fire.
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
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
                Debug.WriteLine("[LoginView] Exit animation failed: " + ex.Message);
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

        private async Task EnsurePairingStartedAsync()
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

        private void Deactivate()
        {
            try
            {
                SessionLogger.Instance.WriteAlways(
                    "[Pairing/UI] LoginView.Deactivate (wasStarted=" + _pairingStarted + ")");
            }
            catch { }
            _pairingStarted = false;
            QrCodePart?.ResetVisualState();
            ViewModel?.DeactivateDiagnostics();
            try { SessionLogger.Instance.PairingTraceActive = false; } catch { }
        }

        private void LoginView_Unloaded(object sender, RoutedEventArgs e)
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

        private async Task StartPairingFlowAsync()
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

        private static void PairingTrace(string message)
        {
            try
            {
                SessionLogger.Instance.WriteAlways("[Pairing/UI] " + (message ?? string.Empty));
            }
            catch
            {
            }
        }
    }
}
