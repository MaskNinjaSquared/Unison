using System;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.ViewModels;
using Windows.ApplicationModel;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Views
{
    /// <summary>
    /// Settings surface hosted in the shell (UserControl View).
    /// </summary>
    public sealed partial class SettingsView : UserControl
    {
        public event EventHandler LeaveRequested;

        private SettingsViewModel _viewModel;
        private bool _shellComboReady;

        public SettingsView()
        {
            this.InitializeComponent();
            this.Loaded += SettingsView_Loaded;
        }

        private SettingsViewModel ViewModel
        {
            get
            {
                if (_viewModel == null && App.Services != null)
                {
                    _viewModel = App.Services.GetRequiredService<SettingsViewModel>();
                    DataContext = _viewModel;
                    _viewModel.LeaveRequested += (s, e) => LeaveRequested?.Invoke(this, e);
                }
                return _viewModel;
            }
        }

        private void SettingsView_Loaded(object sender, RoutedEventArgs e)
        {
            Activate();
        }

        public void Activate()
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

            ViewModel?.Initialize(version);
            SyncShellComboFromViewModel();
        }

        private void SyncShellComboFromViewModel()
        {
            if (ViewModel == null || ShellComboBox == null)
            {
                return;
            }

            _shellComboReady = false;
            int index = ViewModel.SelectedShellIndex;
            if (index < 0 || index >= ShellComboBox.Items.Count)
            {
                index = 0;
            }

            ShellComboBox.SelectedIndex = index;
            _shellComboReady = true;
        }

        private void ShellComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_shellComboReady || ViewModel == null)
            {
                return;
            }

            int index = ShellComboBox.SelectedIndex;
            if (index < 0)
            {
                return;
            }

            if (index == ViewModel.SelectedShellIndex)
            {
                return;
            }

            if (ViewModel.ChangeShellCommand != null &&
                ViewModel.ChangeShellCommand.CanExecute(index))
            {
                ViewModel.ChangeShellCommand.Execute(index);
            }
        }
    }
}
