using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Unison.Uwp.Shell.WhatsAppLegacy.Views
{
    /// <summary>Demand-loaded pivot stub (calls / favorites) for the WhatsApp Legacy shell.</summary>
    public sealed partial class LegacyPivotPlaceholderView : Page
    {
        public LegacyPivotPlaceholderView()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Disabled;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            string section = e.Parameter as string;
            if (!string.IsNullOrWhiteSpace(section) && PlaceholderText != null)
            {
                PlaceholderText.Text = section;
            }
        }
    }
}
