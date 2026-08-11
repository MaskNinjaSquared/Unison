using Windows.ApplicationModel;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.ViewModels;

namespace Unison.Uwp.UI.Views
{
    /// <summary>Welcome screen shown only when logged out; navigates to Login via Get started.</summary>
    public sealed partial class StartPage : Page
    {
        private StartViewModel _vm;

        public StartPage()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Disabled;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility =
                AppViewBackButtonVisibility.Collapsed;

            if (App.Services != null)
            {
                _vm = App.Services.GetRequiredService<StartViewModel>();
                var v = Package.Current.Id.Version;
                _vm.AppVersion = string.Format(
                    "Version {0}.{1}.{2}.{3}",
                    v.Major, v.Minor, v.Build, v.Revision);
                DataContext = _vm;
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
