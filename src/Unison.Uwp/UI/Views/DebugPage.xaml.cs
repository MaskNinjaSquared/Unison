using System;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Constants;
using Unison.Core.ViewModels;

namespace Unison.Uwp.UI.Views
{
    public sealed partial class DebugPage : Page
    {
        public DebugPage()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Disabled;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            DebugPart.BackRequested -= DebugPart_BackRequested;
            DebugPart.BackRequested += DebugPart_BackRequested;
            DebugPart.Activate();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            DebugPart.BackRequested -= DebugPart_BackRequested;
            DebugPart.Deactivate();
        }

        private void DebugPart_BackRequested(object sender, EventArgs e)
        {
            App.Services?.GetService<ShellViewModel>()?.NavigateToSectionCommand.Execute(NavigationRoutes.Chats);
        }
    }
}
