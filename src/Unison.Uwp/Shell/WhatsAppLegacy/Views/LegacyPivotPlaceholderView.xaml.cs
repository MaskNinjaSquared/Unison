using System;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Unison.Uwp.Shell.WhatsAppLegacy.Views
{
    /// <summary>Demand-loaded pivot body (calls / favorites) for the WhatsApp Legacy shell.</summary>
    public sealed partial class LegacyPivotPlaceholderView : Page
    {
        public event EventHandler SearchRequested;

        public event EventHandler ProfileRequested;

        public event EventHandler DebugRequested;

        public event EventHandler SettingsRequested;

        public LegacyPivotPlaceholderView()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Disabled;
            SectionBar.SearchRequested += (s, e) => SearchRequested?.Invoke(this, e);
            SectionBar.ProfileRequested += (s, e) => ProfileRequested?.Invoke(this, e);
            SectionBar.DebugRequested += (s, e) => DebugRequested?.Invoke(this, e);
            SectionBar.SettingsRequested += (s, e) => SettingsRequested?.Invoke(this, e);
        }

        public Task InitAsync(string section)
        {
            bool calls = string.Equals(section, "calls", StringComparison.OrdinalIgnoreCase);
            bool favorites = string.Equals(section, "favorites", StringComparison.OrdinalIgnoreCase);

            CallsEmpty.Visibility = calls ? Visibility.Visible : Visibility.Collapsed;
            FavoritesEmpty.Visibility = favorites ? Visibility.Visible : Visibility.Collapsed;
            SectionBar.Visibility = calls ? Visibility.Visible : Visibility.Collapsed;
            return Task.CompletedTask;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _ = InitAsync(e.Parameter as string);
        }
    }
}
