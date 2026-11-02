using System;
using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Models;
using Unison.Core.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Unison.Uwp.Shell.WhatsAppLegacy.Views
{
    /// <summary>
    /// Legacy Favorites pivot: A–Z chip + shared list + bottom CommandBar (Search / Add).
    /// Unison/WhatsApp use <see cref="UI.Views.FavoritesView"/> (header chrome) instead.
    /// </summary>
    public sealed partial class FavoritesView : Page
    {
        public event EventHandler SearchRequested;

        public event EventHandler ProfileRequested;

        public event EventHandler DebugRequested;

        public event EventHandler SettingsRequested;

        /// <summary>Uses the shared UI.Views args type so Shell DetailHost wiring stays one shape.</summary>
        public event EventHandler<global::Unison.Uwp.UI.Views.ChatSelectedEventArgs> ChatSelected;

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

            FavoritesAppBar.SetAddVisible(true);
            FavoritesAppBar.SearchRequested += (_, __) => SearchRequested?.Invoke(this, EventArgs.Empty);
            FavoritesAppBar.AddRequested += FavoritesAppBar_AddRequested;
            FavoritesAppBar.ProfileRequested += (_, __) => ProfileRequested?.Invoke(this, EventArgs.Empty);
            FavoritesAppBar.DebugRequested += (_, __) => DebugRequested?.Invoke(this, EventArgs.Empty);
            FavoritesAppBar.SettingsRequested += (_, __) => SettingsRequested?.Invoke(this, EventArgs.Empty);

            FavoritesList.ChatSelected += (_, e) => ChatSelected?.Invoke(this, e);
            FavoritesList.HasFavoritesChanged += (_, __) => UpdateSortChip();

            if (ViewModel != null)
            {
                ViewModel.PropertyChanged += ViewModel_PropertyChanged;
                ViewModel.OpenChatRequested += ViewModel_OpenChatRequested;
                FavoritesList.Bind(ViewModel);
                UpdateSortChip();
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

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            if (string.Equals(e.PropertyName, nameof(FavoritesViewModel.SortLabel), StringComparison.Ordinal) ||
                string.Equals(e.PropertyName, nameof(FavoritesViewModel.HasFavorites), StringComparison.Ordinal))
            {
                UpdateSortChip();
            }
        }

        private void UpdateSortChip()
        {
            bool has = ViewModel != null && ViewModel.HasFavorites;
            SortChip.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            SortLabelText.Text = ViewModel?.SortLabel ?? "A-Z";
        }

        private void FavoritesAppBar_AddRequested(object sender, EventArgs e)
        {
            if (ViewModel?.OpenPickerCommand != null && ViewModel.OpenPickerCommand.CanExecute(null))
            {
                ViewModel.OpenPickerCommand.Execute(null);
            }
        }

        private void ViewModel_OpenChatRequested(object sender, ChatItem chat)
        {
            if (chat != null)
            {
                ChatSelected?.Invoke(
                    this,
                    new global::Unison.Uwp.UI.Views.ChatSelectedEventArgs(chat));
            }
        }
    }
}
