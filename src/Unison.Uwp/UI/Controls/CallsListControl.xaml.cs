using System;
using System.ComponentModel;
using Unison.Core.Models;
using Unison.Core.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Favourites + Recents list body for Calls (shared by Unison/WhatsApp header host and Legacy).
    /// </summary>
    public sealed partial class CallsListControl : UserControl
    {
        private CallsViewModel _viewModel;
        private bool _hooked;

        public CallsListControl()
        {
            InitializeComponent();
        }

        public void Bind(CallsViewModel viewModel)
        {
            if (_hooked && _viewModel != null)
            {
                _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
                _hooked = false;
            }

            _viewModel = viewModel;
            DataContext = viewModel;
            if (_viewModel == null)
            {
                FavoritesList.ItemsSource = null;
                RecentsList.ItemsSource = null;
                return;
            }

            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            _hooked = true;
            FavoritesList.ItemsSource = _viewModel.FavoritePreview;
            RecentsList.ItemsSource = _viewModel.Recents;
            UpdateSections();
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_viewModel == null || e == null)
            {
                return;
            }

            if (string.Equals(e.PropertyName, nameof(CallsViewModel.FavoritePreview), StringComparison.Ordinal))
            {
                FavoritesList.ItemsSource = _viewModel.FavoritePreview;
            }

            if (string.Equals(e.PropertyName, nameof(CallsViewModel.Recents), StringComparison.Ordinal))
            {
                RecentsList.ItemsSource = _viewModel.Recents;
            }

            if (string.Equals(e.PropertyName, nameof(CallsViewModel.ShowEmptyState), StringComparison.Ordinal) ||
                string.Equals(e.PropertyName, nameof(CallsViewModel.ShowFavoritesSection), StringComparison.Ordinal) ||
                string.Equals(e.PropertyName, nameof(CallsViewModel.ShowRecentsSection), StringComparison.Ordinal) ||
                string.Equals(e.PropertyName, nameof(CallsViewModel.ShowFavoritesSeeAll), StringComparison.Ordinal) ||
                string.Equals(e.PropertyName, nameof(CallsViewModel.HasFavorites), StringComparison.Ordinal) ||
                string.Equals(e.PropertyName, nameof(CallsViewModel.HasRecents), StringComparison.Ordinal))
            {
                UpdateSections();
            }
        }

        private void UpdateSections()
        {
            if (_viewModel == null)
            {
                return;
            }

            FavoritesSection.Visibility = _viewModel.ShowFavoritesSection
                ? Visibility.Visible
                : Visibility.Collapsed;
            RecentsSection.Visibility = _viewModel.ShowRecentsSection
                ? Visibility.Visible
                : Visibility.Collapsed;
            SeeAllFavoritesButton.Visibility = _viewModel.ShowFavoritesSeeAll
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void SeeAllFavoritesButton_Click(object sender, RoutedEventArgs e)
        {
            _viewModel?.ShowAllFavoritesCommand?.Execute(null);
        }

        private void FavoritesList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as CallFavoriteItem;
            if (item != null)
            {
                _viewModel?.SelectFavoriteCommand?.Execute(item);
            }
        }

        private void FavoriteVoiceCallButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as FrameworkElement;
            var item = button?.DataContext as CallFavoriteItem;
            if (item != null &&
                _viewModel?.CallFavoriteVoiceCommand != null &&
                _viewModel.CallFavoriteVoiceCommand.CanExecute(item))
            {
                _viewModel.CallFavoriteVoiceCommand.Execute(item);
            }
        }

        private void RecentsList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as CallRecentItem;
            if (item != null)
            {
                _viewModel?.SelectRecentCommand?.Execute(item);
            }
        }
    }
}
