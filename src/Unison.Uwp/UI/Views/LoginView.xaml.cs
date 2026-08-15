using System;
using System.ComponentModel;
using Windows.UI.Core;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.ViewModels;
using Unison.Uwp.Client;

namespace Unison.Uwp.UI.Views
{
    /// <summary>
    /// Full-page QR / pairing. Root back stack is cleared — system Back does not leave this page
    /// toward Shell or Start (handled + button hidden).
    /// </summary>
    public sealed partial class LoginView : Page
    {
        private ShellViewModel _shell;
        private bool _backHooked;
        private bool _exitTransitionRunning;

        public LoginView()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Disabled;
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

            LoginPart?.Deactivate();
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
                LoginPart?.Deactivate();
                if (LoginPart != null)
                {
                    await LoginPart.PlayExitTransitionAsync();
                }

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
            if (LoginPart == null)
            {
                return;
            }

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
                LoginPart.Deactivate();
                return;
            }

            LoginPart.Deactivate();
            if (_shell.StartPairingOnLoginSurface)
            {
                PairingTrace("ApplyPairingState → EnsurePairingStartedAsync");
                _ = LoginPart.EnsurePairingStartedAsync();
            }
            else
            {
                PairingTrace("ApplyPairingState → wait (StartPairingOnLoginSurface=false)");
            }
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
