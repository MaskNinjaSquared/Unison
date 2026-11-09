using System;
using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Constants;
using Unison.Core.Models;
using Unison.Core.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Unison.Uwp.UI.Views
{
    /// <summary>
    /// Unison/WhatsApp Favourites: header + shared list + overlay picker.
    /// Legacy pivot uses <see cref="Shell.WhatsAppLegacy.Views.FavoritesView"/> instead.
    /// </summary>
    public sealed partial class FavoritesView : Page
    {
        public event EventHandler MenuClicked;

        public event EventHandler SearchRequested;

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

            FavoritesList.ChatSelected += (_, e) => ChatSelected?.Invoke(this, e);
            FavoritesList.HasFavoritesChanged += (_, __) => UpdateHeaderChrome();

            if (ViewModel != null)
            {
                ViewModel.PropertyChanged += ViewModel_PropertyChanged;
                ViewModel.OpenChatRequested += ViewModel_OpenChatRequested;
                FavoritesList.Bind(ViewModel);
                UpdateHeaderChrome();
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

            if (string.Equals(e.PropertyName, nameof(FavoritesViewModel.SortLabel), StringComparison.Ordinal))
            {
                SortLabelText.Text = ViewModel.SortLabel ?? "A-Z";
            }

            if (string.Equals(e.PropertyName, nameof(FavoritesViewModel.HasFavorites), StringComparison.Ordinal))
            {
                UpdateHeaderChrome();
            }
        }

        private void UpdateHeaderChrome()
        {
            bool has = ViewModel != null && ViewModel.HasFavorites;
            SortChip.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            AddFavoriteHeaderButton.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            SortLabelText.Text = ViewModel?.SortLabel ?? "A-Z";
        }

        private void SectionBackButton_Click(object sender, RoutedEventArgs e)
        {
            var shell = App.Services?.GetService<ShellViewModel>();
            if (shell != null && shell.TryHandleShellBack())
            {
                return;
            }

            shell?.NavigateToSectionCommand?.Execute(NavigationRoutes.Chats);
        }

        private void MenuButton_Click(object sender, RoutedEventArgs e)
        {
            MenuClicked?.Invoke(this, EventArgs.Empty);
        }

        private void SearchHeaderButton_Click(object sender, RoutedEventArgs e)
        {
            SearchRequested?.Invoke(this, EventArgs.Empty);
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
