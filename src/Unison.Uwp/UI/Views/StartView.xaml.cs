using System.Diagnostics;
using Windows.ApplicationModel;
using Windows.UI.Core;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.ViewModels;

namespace Unison.Uwp.UI.Views
{
    /// <summary>Welcome screen shown only when logged out; navigates to Login via Get started.</summary>
    public sealed partial class StartView : Page
    {
        private StartViewModel _vm;
        private bool _syncingLanguageCombo;

        public StartView()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Disabled;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility =
                AppViewBackButtonVisibility.Collapsed;

            if (App.Services == null)
            {
                return;
            }

            _vm = App.Services.GetRequiredService<StartViewModel>();
            var v = Package.Current.Id.Version;
            _vm.AppVersion = string.Format(
                "Version {0}.{1}.{2}.{3}",
                v.Major, v.Minor, v.Build, v.Revision);

            // DataContext / ItemsSource / SelectedItem rebinds fire SelectionChanged —
            // suppress until the persisted index is restored.
            _syncingLanguageCombo = true;
            try
            {
                DataContext = _vm;
                _vm.RefreshLanguageSelection();
                SyncLanguageComboSelection();
            }
            finally
            {
                _syncingLanguageCombo = false;
            }
        }

        /// <summary>
        /// UWP ComboBox often clears selection when ItemsSource binds after SelectedIndex/Item.
        /// </summary>
        private void SyncLanguageComboSelection()
        {
            if (_vm == null || LanguageComboBox == null)
            {
                return;
            }

            int index = _vm.SelectedLanguageIndex;
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

        /// <summary>
        /// Read SelectedIndex from the ComboBox directly. InvokeCommandAction + CommandParameter
        /// binding often passes a stale index on SelectionChanged (UI shows ES/IT, saves en/pt).
        /// </summary>
        private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingLanguageCombo || _vm == null || LanguageComboBox == null)
            {
                return;
            }

            int index = LanguageComboBox.SelectedIndex;
            if (index < 0)
            {
                return;
            }

            // Rebind / SelectedItem noise often re-selects the already-saved language.
            if (index == _vm.SelectedLanguageIndex)
            {
                return;
            }

            Debug.WriteLine("[StartView] LanguageComboBox → index=" + index);
            if (_vm.ChangeLanguageCommand != null && _vm.ChangeLanguageCommand.CanExecute(index))
            {
                _vm.ChangeLanguageCommand.Execute(index);
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            DataContext = null;
            _vm = null;
        }
    }
}
