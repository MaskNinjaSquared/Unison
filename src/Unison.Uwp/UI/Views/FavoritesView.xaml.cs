using System;
using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Navigation;

namespace Unison.Uwp.UI.Views
{
    /// <summary>Shell Favourites: empty CTA, A–Z SemanticZoom list, overlay picker.</summary>
    public sealed partial class FavoritesView : Page
    {
        private readonly CollectionViewSource _favoritesCvs = new CollectionViewSource
        {
            IsSourceGrouped = true
        };

        public event EventHandler MenuClicked;

        /// <summary>Same shape as <see cref="ChatListView.ChatSelected"/> for shell wiring.</summary>
        public event EventHandler<ChatSelectedEventArgs> ChatSelected;

        public FavoritesViewModel ViewModel { get; private set; }

        public FavoritesView()
        {
            if (App.Services != null)
            {
                ViewModel = App.Services.GetService<FavoritesViewModel>();
            }

            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Disabled;
            DataContext = ViewModel;

            if (ViewModel != null)
            {
                ViewModel.PropertyChanged += ViewModel_PropertyChanged;
                ViewModel.OpenChatRequested += ViewModel_OpenChatRequested;
                BindFavoritesSource();
                UpdateEmptyVsList();
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
                ViewModel.OpenChatRequested -= ViewModel_OpenChatRequested;
            }

            base.OnNavigatedFrom(e);
        }

        private void BindFavoritesSource()
        {
            _favoritesCvs.Source = ViewModel.GroupedFavorites;
            FavoritesList.ItemsSource = _favoritesCvs.View;
            FavoritesJumpList.ItemsSource = ViewModel.JumpTiles;
            SortLabelText.Text = ViewModel.SortLabel ?? "A-Z";
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            if (string.Equals(e.PropertyName, nameof(FavoritesViewModel.GroupedFavorites), StringComparison.Ordinal))
            {
                _favoritesCvs.Source = ViewModel.GroupedFavorites;
            }

            if (string.Equals(e.PropertyName, nameof(FavoritesViewModel.JumpTiles), StringComparison.Ordinal))
            {
                FavoritesJumpList.ItemsSource = ViewModel.JumpTiles;
            }

            if (string.Equals(e.PropertyName, nameof(FavoritesViewModel.HasFavorites), StringComparison.Ordinal) ||
                string.Equals(e.PropertyName, nameof(FavoritesViewModel.ShowEmptyState), StringComparison.Ordinal))
            {
                UpdateEmptyVsList();
            }

            if (string.Equals(e.PropertyName, nameof(FavoritesViewModel.SortLabel), StringComparison.Ordinal))
            {
                SortLabelText.Text = ViewModel.SortLabel ?? "A-Z";
            }
        }

        private void UpdateEmptyVsList()
        {
            bool has = ViewModel != null && ViewModel.HasFavorites;
            EmptyState.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            FavoritesZoom.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            SortChip.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            AddFavoriteHeaderButton.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        }

        private void MenuButton_Click(object sender, RoutedEventArgs e)
        {
            MenuClicked?.Invoke(this, EventArgs.Empty);
        }

        private void FavoritesList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var chat = e.ClickedItem as ChatItem;
            if (chat != null)
            {
                ChatSelected?.Invoke(this, new ChatSelectedEventArgs(chat));
            }
        }

        private void FavoritesJumpList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var tile = e.ClickedItem as AlphabetJumpTile;
            if (tile == null || !tile.IsEnabled || tile.Group == null)
            {
                return;
            }

            FavoritesZoom.IsZoomedInViewActive = true;
            try
            {
                FavoritesList.ScrollIntoView(tile.Group, ScrollIntoViewAlignment.Leading);
            }
            catch
            {
                // Group may not be realized yet; zoom-in alone is still useful.
            }
        }

        private void ViewModel_OpenChatRequested(object sender, ChatItem chat)
        {
            if (chat != null)
            {
                ChatSelected?.Invoke(this, new ChatSelectedEventArgs(chat));
            }
        }
    }
}
