using System;
using System.ComponentModel;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.ViewModels;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Data;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Favourites list body (empty CTA + A–Z SemanticZoom) shared by Unison/WhatsApp header host and Legacy.
    /// </summary>
    public sealed partial class FavoritesListControl : UserControl
    {
        private readonly CollectionViewSource _favoritesCvs = new CollectionViewSource
        {
            IsSourceGrouped = true
        };

        private FavoritesViewModel _viewModel;
        private bool _hooked;
        private AlphabetChatGroup _pendingJumpGroup;

        public event EventHandler<ChatSelectedEventArgs> ChatSelected;

        public event EventHandler HasFavoritesChanged;

        public bool HasFavorites => _viewModel != null && _viewModel.HasFavorites;

        public FavoritesListControl()
        {
            InitializeComponent();
        }

        public void Bind(FavoritesViewModel viewModel)
        {
            if (_hooked && _viewModel != null)
            {
                _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
                _hooked = false;
            }

            _viewModel = viewModel;
            if (_viewModel == null)
            {
                EmptyState.AddCommand = null;
                FavoritesList.ItemsSource = null;
                FavoritesJumpList.ItemsSource = null;
                UpdateEmptyVsList();
                return;
            }

            EmptyState.AddCommand = _viewModel.OpenPickerCommand;
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            _hooked = true;
            BindFavoritesSource();
            UpdateEmptyVsList();
        }

        private void BindFavoritesSource()
        {
            if (_viewModel == null)
            {
                return;
            }

            _favoritesCvs.Source = _viewModel.GroupedFavorites;
            FavoritesList.ItemsSource = _favoritesCvs.View;
            FavoritesJumpList.ItemsSource = _viewModel.JumpTiles;
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_viewModel == null || e == null)
            {
                return;
            }

            if (string.Equals(e.PropertyName, nameof(FavoritesViewModel.GroupedFavorites), StringComparison.Ordinal) ||
                string.Equals(e.PropertyName, nameof(FavoritesViewModel.JumpTiles), StringComparison.Ordinal))
            {
                BindFavoritesSource();
            }

            if (string.Equals(e.PropertyName, nameof(FavoritesViewModel.HasFavorites), StringComparison.Ordinal) ||
                string.Equals(e.PropertyName, nameof(FavoritesViewModel.ShowEmptyState), StringComparison.Ordinal))
            {
                UpdateEmptyVsList();
                HasFavoritesChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void UpdateEmptyVsList()
        {
            bool has = HasFavorites;
            EmptyState.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            FavoritesZoom.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
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
            }
        }
    }
}
