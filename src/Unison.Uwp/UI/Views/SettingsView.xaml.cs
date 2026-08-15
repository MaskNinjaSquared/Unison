using System;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Constants;
using Unison.Core.ViewModels;

namespace Unison.Uwp.UI.Views
{
    public sealed partial class SettingsView : Page
    {
        public SettingsView()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Disabled;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            SettingsPart.LeaveRequested -= SettingsPart_LeaveRequested;
            SettingsPart.LeaveRequested += SettingsPart_LeaveRequested;
            SettingsPart.Activate();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            SettingsPart.LeaveRequested -= SettingsPart_LeaveRequested;
        }

        private void SettingsPart_LeaveRequested(object sender, EventArgs e)
        {
            App.Services?.GetService<ShellViewModel>()?.NavigateToSectionCommand.Execute(NavigationRoutes.Chats);
        }
    }
}
