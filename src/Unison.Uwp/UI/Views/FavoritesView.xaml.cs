using System;
using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.ViewModels;
using Windows.UI.Core;
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

        private AlphabetChatGroup _pendingJumpGroup;

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
            if (tile == null || !tile.IsEnabled || tile.Group == null || tile.Group.Count == 0)
            {
                return;
            }

            // ScrollIntoView(group) fails inside SemanticZoom — scroll the first ChatItem via
            // MakeVisible after the zoom transition (same fix as ContactsVisualizerControl).
            _pendingJumpGroup = tile.Group;
            if (!FavoritesZoom.IsZoomedInViewActive)
            {
                FavoritesZoom.IsZoomedInViewActive = true;
                return;
            }

            ScheduleScrollToPendingGroup();
        }

        private void FavoritesZoom_ViewChangeStarted(object sender, SemanticZoomViewChangedEventArgs e)
        {
            if (e == null || e.IsSourceZoomedInView)
            {
                return;
            }

            AlphabetChatGroup pending = _pendingJumpGroup;
            if (pending != null && pending.Count > 0)
            {
                e.DestinationItem.Item = pending[0];
                return;
            }

            var tile = e.SourceItem?.Item as AlphabetJumpTile;
            if (tile?.Group != null && tile.Group.Count > 0)
            {
                e.DestinationItem.Item = tile.Group[0];
                _pendingJumpGroup = tile.Group;
            }
        }

        private void FavoritesZoom_ViewChangeCompleted(object sender, SemanticZoomViewChangedEventArgs e)
        {
            if (e == null || e.IsSourceZoomedInView || _pendingJumpGroup == null)
            {
                return;
            }

            ScheduleScrollToPendingGroup();
        }

        private void ScheduleScrollToPendingGroup()
        {
            AlphabetChatGroup group = _pendingJumpGroup;
            if (group == null || group.Count == 0)
            {
                return;
            }

            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            {
                if (!ReferenceEquals(_pendingJumpGroup, group))
                {
                    return;
                }

                ScrollToGroup(group);
                _pendingJumpGroup = null;
            });
        }

        private void ScrollToGroup(AlphabetChatGroup group)
        {
            if (group == null || group.Count == 0)
            {
                return;
            }

            ChatItem first = group[0];
            try
            {
                FavoritesList.UpdateLayout();
                FavoritesList.MakeVisible(new SemanticZoomLocation { Item = first });
                FavoritesList.ScrollIntoView(first, ScrollIntoViewAlignment.Leading);
            }
            catch
            {
                // Container may still be unrealized; zoom-in alone is still useful.
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
