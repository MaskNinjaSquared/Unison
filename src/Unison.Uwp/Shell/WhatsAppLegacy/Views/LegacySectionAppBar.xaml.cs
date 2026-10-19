using System;
using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.Shell.WhatsAppLegacy.Views
{
    /// <summary>
    /// Bottom bar for Legacy pivots that only open search chrome, plus profile, debug, and settings.
    /// </summary>
    public sealed partial class LegacySectionAppBar : UserControl
    {
        private ShellViewModel _shell;

        public event EventHandler SearchRequested;

        public event EventHandler ProfileRequested;

        public event EventHandler DebugRequested;

        public event EventHandler SettingsRequested;

        public LegacySectionAppBar()
        {
            _shell = App.Services?.GetService<ShellViewModel>();
            InitializeComponent();
            Loaded += LegacySectionAppBar_Loaded;
            Unloaded += LegacySectionAppBar_Unloaded;
        }

        private void LegacySectionAppBar_Loaded(object sender, RoutedEventArgs e)
        {
            if (_shell != null)
            {
                _shell.PropertyChanged -= Shell_PropertyChanged;
                _shell.PropertyChanged += Shell_PropertyChanged;
            }

            RefreshProfileSecondary();
        }

        private void LegacySectionAppBar_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_shell != null)
            {
                _shell.PropertyChanged -= Shell_PropertyChanged;
            }
        }

        private void Shell_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ShellViewModel.CurrentUserName) ||
                e.PropertyName == nameof(ShellViewModel.CurrentUserAvatar))
            {
                RefreshProfileSecondary();
            }
        }

        private void RefreshProfileSecondary() =>
            LegacyProfileSecondary.Apply(ProfileSecondaryButton, _shell);

        private void Search_Click(object sender, RoutedEventArgs e)
        {
            SearchRequested?.Invoke(this, EventArgs.Empty);
        }

        private void Profile_Click(object sender, RoutedEventArgs e)
        {
            ProfileRequested?.Invoke(this, EventArgs.Empty);
        }

        private void Debug_Click(object sender, RoutedEventArgs e)
        {
            DebugRequested?.Invoke(this, EventArgs.Empty);
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            SettingsRequested?.Invoke(this, EventArgs.Empty);
        }

        private void Bar_Loaded(object sender, RoutedEventArgs e)
        {
            var bar = sender as CommandBar;
            if (bar == null)
            {
                return;
            }

            LegacyCommandBarLayout.CenterPrimaryCommands(bar);
        }
    }
}
