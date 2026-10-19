using System;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.ViewModels;
using Windows.ApplicationModel;
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
        private LegacySettingsViewModel _viewModel;
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

        private LegacySettingsViewModel ViewModel
        {
            get
            {
                if (_viewModel == null && App.Services != null)
                {
                    _viewModel = App.Services.GetRequiredService<LegacySettingsViewModel>();
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
            ViewModel?.OpenCategory(LegacySettingsViewModel.Profile);
            ApplyCategoryVisibility();
        }

        private void HookLeaveRequested()
        {
            SettingsViewModel vm = ViewModel;
            if (vm == null || _leaveHooked)
            {
                return;
            }

            vm.LeaveRequested += ViewModel_LeaveRequested;
            vm.PropertyChanged += ViewModel_PropertyChanged;
            _leaveHooked = true;
        }

        private void UnhookLeaveRequested()
        {
            if (!_leaveHooked || _viewModel == null)
            {
                return;
            }

            _viewModel.LeaveRequested -= ViewModel_LeaveRequested;
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _leaveHooked = false;
        }

        private void ViewModel_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LegacySettingsViewModel.OpenCategoryId))
            {
                ApplyCategoryVisibility();
            }
        }

        private void ViewModel_LeaveRequested(object sender, EventArgs e)
        {
            if (ViewModel != null && ViewModel.TryCloseCategory())
            {
                return;
            }

            // Do not NavigateToSection(Chats): that remounts the chat list inside the overlay frame.
            ShellView.CloseOverlayFrom(this);
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null && ViewModel.LeaveCommand != null && ViewModel.LeaveCommand.CanExecute(null))
            {
                ViewModel.LeaveCommand.Execute(null);
            }
        }

        private void Category_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            ViewModel?.OpenCategory(button?.Tag as string);
        }

        private void ApplyCategoryVisibility()
        {
            string id = ViewModel?.OpenCategoryId;
            SetCategoryVisible(RootCategories, id == null);
            SetCategoryVisible(ProfileCategory, id == LegacySettingsViewModel.Profile);
            SetCategoryVisible(GeneralCategory, id == LegacySettingsViewModel.General);
            SetCategoryVisible(NotificationsCategory, id == LegacySettingsViewModel.Notifications);
            SetCategoryVisible(CustomizationCategory, id == LegacySettingsViewModel.Customization);
            SetCategoryVisible(AccountCategory, id == LegacySettingsViewModel.Account);
        }

        private static void SetCategoryVisible(UIElement element, bool show)
        {
            if (element != null)
            {
                element.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void SettingsView_Loaded(object sender, RoutedEventArgs e)
        {
            Activate();
            ViewModel?.Attach();
            ApplyCategoryVisibility();

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

    }
}
