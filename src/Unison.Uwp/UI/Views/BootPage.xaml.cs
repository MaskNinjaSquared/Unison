using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.ViewModels;
using Unison.Uwp.Services;
using Unison.Uwp.Services.WhatsApp;

namespace Unison.Uwp.UI.Views
{
    /// <summary>
    /// Lightweight boot surface: resolves session then navigates to Start or AppShell.
    /// </summary>
    public sealed partial class BootPage : Page
    {
        public BootPage()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Disabled;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            try
            {
                (App.GetWhatsAppService() as WhatsAppService)?.AttachUiDispatcher(Dispatcher);
                var shell = App.Services?.GetRequiredService<ShellViewModel>();
                if (shell != null)
                {
                    await shell.InitializeAsync();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[BootPage] Initialize failed: " + ex);
            }
        }
    }
}
