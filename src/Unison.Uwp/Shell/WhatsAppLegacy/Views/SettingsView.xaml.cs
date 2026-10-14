using System;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Constants;
using Unison.Core.ViewModels;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Resources;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Unison.Uwp.Shell.WhatsAppLegacy.Views
{
    /// <summary>
    /// WhatsApp Legacy settings page (WP8 typography). Opened via shell ContentFrame navigation.
    /// </summary>
    public sealed partial class SettingsView : Page
    {
        private SettingsViewModel _viewModel;
        private bool _syncingLanguageCombo;
        private bool _leaveHooked;
        private bool _openProfileOnLoad;

        public SettingsView()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Disabled;
            Loaded += SettingsView_Loaded;
            Unloaded += SettingsView_Unloaded;
        }

        private SettingsViewModel ViewModel
        {
            get
            {
                if (_viewModel == null && App.Services != null)
                {
                    _viewModel = App.Services.GetRequiredService<SettingsViewModel>();
                    DataContext = _viewModel;
                }

                return _viewModel;
            }
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _openProfileOnLoad = e.Parameter is bool openProfile && openProfile;
            HookLeaveRequested();
            Activate();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            UnhookLeaveRequested();
        }

        /// <summary>Opens the profile pane when settings is already on screen.</summary>
        public void OpenProfile()
        {
            ShowProfile(true);
        }

        private void HookLeaveRequested()
        {
            SettingsViewModel vm = ViewModel;
            if (vm == null || _leaveHooked)
            {
                return;
            }

            vm.LeaveRequested += ViewModel_LeaveRequested;
            _leaveHooked = true;
        }

        private void UnhookLeaveRequested()
        {
            if (!_leaveHooked || _viewModel == null)
            {
                return;
            }

            _viewModel.LeaveRequested -= ViewModel_LeaveRequested;
            _leaveHooked = false;
        }

        private void ViewModel_LeaveRequested(object sender, EventArgs e)
        {
            if (ProfilePane != null && ProfilePane.Visibility == Visibility.Visible)
            {
                ShowProfile(false);
                return;
            }

            App.Services?.GetService<ShellViewModel>()?.NavigateToSectionCommand.Execute(NavigationRoutes.Chats);
        }

        private void SettingsView_Loaded(object sender, RoutedEventArgs e)
        {
            Activate();
            ViewModel?.Attach();
            ApplyYourProfileTitle();
            if (PageTitle != null && !string.IsNullOrEmpty(PageTitle.Text))
            {
                PageTitle.Text = PageTitle.Text.ToUpperInvariant();
            }

            if (_openProfileOnLoad)
            {
                _openProfileOnLoad = false;
                OpenProfile();
            }
        }

        private void SettingsView_Unloaded(object sender, RoutedEventArgs e)
        {
            _viewModel?.Detach();
        }

        private void Activate()
        {
            string version = "?";
            try
            {
                var v = Package.Current.Id.Version;
                version = $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
            }
            catch
            {
            }

            _syncingLanguageCombo = true;
            try
            {
                ViewModel?.Initialize(version);
                SyncLanguageComboSelection();
            }
            finally
            {
                _syncingLanguageCombo = false;
            }
        }

        private void ApplyYourProfileTitle()
        {
            if (YourProfileBox == null)
            {
                return;
            }

            try
            {
                ResourceLoader loader;
                try { loader = ResourceLoader.GetForCurrentView(); }
                catch { loader = ResourceLoader.GetForViewIndependentUse(); }

                string title = loader.GetString("Settings_YourProfile/Text");
                if (!string.IsNullOrEmpty(title))
                {
                    YourProfileBox.Title = title;
                }
            }
            catch
            {
            }
        }

        private void SyncLanguageComboSelection()
        {
            if (ViewModel == null || LanguageComboBox == null)
            {
                return;
            }

            int index = ViewModel.SelectedLanguageIndex;
            if (index < 0 || index >= LanguageComboBox.Items.Count)
            {
                return;
            }

            bool wasSyncing = _syncingLanguageCombo;
            _syncingLanguageCombo = true;
            try
            {
                LanguageComboBox.SelectedIndex = index;
            }
            finally
            {
                _syncingLanguageCombo = wasSyncing;
            }
        }

        private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingLanguageCombo || ViewModel == null || LanguageComboBox == null)
            {
                return;
            }

            int index = LanguageComboBox.SelectedIndex;
            if (index < 0 || index == ViewModel.SelectedLanguageIndex)
            {
                return;
            }

            Debug.WriteLine("[LegacySettings] LanguageComboBox → index=" + index);
            if (ViewModel.ChangeLanguageCommand != null &&
                ViewModel.ChangeLanguageCommand.CanExecute(index))
            {
                ViewModel.ChangeLanguageCommand.Execute(index);
            }
        }

        private void YourProfileBox_Click(object sender, EventArgs e) => ShowProfile(true);

        private void CloseProfile_Click(object sender, RoutedEventArgs e) => ShowProfile(false);

        private void ShowProfile(bool show)
        {
            if (ProfilePane == null || SettingsScroll == null || PageHeader == null)
            {
                return;
            }

            ProfilePane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            SettingsScroll.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
            PageHeader.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
