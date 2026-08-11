using System.ComponentModel;
using Windows.UI.Core;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.ViewModels;

namespace Unison.Uwp.UI.Views
{
    /// <summary>
    /// Full-page QR / pairing. Root back stack is cleared — system Back does not leave this page
    /// toward Shell or Start (handled + button hidden).
    /// </summary>
    public sealed partial class LoginPage : Page
    {
        private ShellViewModel _shell;
        private bool _backHooked;

        public LoginPage()
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
                SystemNavigationManager.GetForCurrentView().BackRequested += LoginPage_BackRequested;
                _backHooked = true;
            }

            _shell = App.Services?.GetService<ShellViewModel>();
            if (_shell != null)
            {
                _shell.PropertyChanged -= Shell_PropertyChanged;
                _shell.PropertyChanged += Shell_PropertyChanged;
            }

            ApplyPairingState();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            if (_shell != null)
            {
                _shell.PropertyChanged -= Shell_PropertyChanged;
                _shell = null;
            }

            if (_backHooked)
            {
                SystemNavigationManager.GetForCurrentView().BackRequested -= LoginPage_BackRequested;
                _backHooked = false;
            }

            LoginPart?.Deactivate();
        }

        private void LoginPage_BackRequested(object sender, BackRequestedEventArgs e)
        {
            // QR is a root terminal page: never GoBack from here.
            e.Handled = true;
        }

        private void Shell_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ShellViewModel.AppSurface) ||
                e.PropertyName == nameof(ShellViewModel.StartPairingOnLoginSurface))
            {
                ApplyPairingState();
            }
        }

        private void ApplyPairingState()
        {
            if (LoginPart == null)
            {
                return;
            }

            LoginPart.Deactivate();
            if (_shell != null && _shell.StartPairingOnLoginSurface)
            {
                _ = LoginPart.EnsurePairingStartedAsync();
            }
        }
    }
}
