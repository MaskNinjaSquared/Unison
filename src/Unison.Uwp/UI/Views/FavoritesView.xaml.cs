using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Unison.Uwp.UI.Views
{
    /// <summary>Shell content: Favorites empty state (Unison / WhatsApp sidebar).</summary>
    public sealed partial class FavoritesView : Page
    {
        public event EventHandler MenuClicked;

        public FavoritesView()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Disabled;
        }

        private void MenuButton_Click(object sender, RoutedEventArgs e)
        {
            MenuClicked?.Invoke(this, EventArgs.Empty);
        }
    }
}
