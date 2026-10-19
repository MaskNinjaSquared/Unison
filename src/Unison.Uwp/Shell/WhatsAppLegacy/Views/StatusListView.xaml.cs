using System;
using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Contracts;
using Unison.Core.Models;
using Unison.Core.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.Shell.WhatsAppLegacy.Views
{
    public sealed class StatusAuthorSelectedEventArgs : EventArgs
    {
        public StatusAuthorItem Author { get; }

        public StatusAuthorSelectedEventArgs(StatusAuthorItem author)
        {
            Author = author;
        }
    }

    public sealed partial class StatusListView : UserControl
    {
        private bool _hooked;
        private bool _suppressSelectionChanged;
        private ShellViewModel _shell;

        public StatusListViewModel ViewModel { get; private set; }

        public event EventHandler<StatusAuthorSelectedEventArgs> AuthorSelected;

        public event EventHandler MenuClicked;

        public event EventHandler SelectionCleared;

        public event EventHandler SearchRequested;

        public event EventHandler ProfileRequested;

        public event EventHandler DebugRequested;

        public event EventHandler SettingsRequested;

        public StatusListView()
        {
            if (App.Services != null)
            {
                ViewModel = App.Services.GetRequiredService<StatusListViewModel>();
                DataContext = ViewModel;
                _shell = App.Services.GetService<ShellViewModel>();
            }

            InitializeComponent();
            Loaded += StatusListView_Loaded;
            Unloaded += StatusListView_Unloaded;
            ApplyFilterLabel();
        }

        private void ApplyFilterLabel()
        {
            if (FilterLabel == null)
            {
                return;
            }

            var strings = App.Services?.GetService<IStringResources>();
            FilterLabel.Text = strings?.Get("StatusList_FilterLink.Text", "Filter Status")
                ?? "Filter Status";
        }

        /// <summary>
        /// Stock MenuFlyout only — same Creators Update constraint as Legacy ChatList.
        /// </summary>
        private void FilterButton_Click(object sender, RoutedEventArgs e)
        {
            if (FilterButton == null || ViewModel?.FilterStatusCommand == null)
            {
                return;
            }

            var strings = App.Services?.GetService<IStringResources>();
            var flyout = new MenuFlyout();
            flyout.Items.Add(CreateFilterItem(
                strings?.Get("StatusList_FilterAll.Text", "All") ?? "All", 0));
            flyout.Items.Add(CreateFilterItem(
                strings?.Get("StatusList_FilterRecent.Text", "Recent") ?? "Recent", 1));
            flyout.Items.Add(CreateFilterItem(
                strings?.Get("StatusList_FilterViewed.Text", "Viewed") ?? "Viewed", 2));
            flyout.ShowAt(FilterButton);
        }

        private MenuFlyoutItem CreateFilterItem(string text, int filter)
        {
            return new MenuFlyoutItem
            {
                Text = text,
                Command = ViewModel.FilterStatusCommand,
                CommandParameter = filter
            };
        }

        private void StatusCommandBar_Loaded(object sender, RoutedEventArgs e)
        {
            LegacyCommandBarLayout.CenterPrimaryCommands(StatusCommandBar);
        }

        private void SearchAppBar_Click(object sender, RoutedEventArgs e) =>
            SearchRequested?.Invoke(this, EventArgs.Empty);

        private void ProfileAppBar_Click(object sender, RoutedEventArgs e) =>
            ProfileRequested?.Invoke(this, EventArgs.Empty);

        private void DebugAppBar_Click(object sender, RoutedEventArgs e) =>
            DebugRequested?.Invoke(this, EventArgs.Empty);

        private void SettingsAppBar_Click(object sender, RoutedEventArgs e) =>
            SettingsRequested?.Invoke(this, EventArgs.Empty);

        private void StatusListView_Loaded(object sender, RoutedEventArgs e)
        {
            if (_shell != null)
            {
                _shell.PropertyChanged -= Shell_PropertyChanged;
                _shell.PropertyChanged += Shell_PropertyChanged;
            }

            RefreshProfileSecondary();

            if (ViewModel == null || _hooked)
            {
                return;
            }

            _hooked = true;
            ViewModel.SelectionCleared += ViewModel_SelectionCleared;
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
            ViewModel.Attach();
            SyncSelectionFromViewModel();
        }

        private void StatusListView_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_shell != null)
            {
                _shell.PropertyChanged -= Shell_PropertyChanged;
            }

            if (ViewModel == null || !_hooked)
            {
                return;
            }

            _hooked = false;
            ViewModel.SelectionCleared -= ViewModel_SelectionCleared;
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            ViewModel.Detach();
        }

        private void Shell_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ShellViewModel.CurrentUserName) ||
                e.PropertyName == nameof(ShellViewModel.CurrentUserAvatar))
            {
                RefreshProfileSecondary();
            }
        }

        private void RefreshProfileSecondary() =>
            LegacyProfileSecondary.Apply(ProfileSecondaryButton, _shell);

        public void ClearSelection()
        {
            _suppressSelectionChanged = true;
            try
            {
                AuthorList.SelectedItem = null;
                if (ViewModel != null)
                {
                    ViewModel.SelectedAuthor = null;
                }
            }
            finally
            {
                _suppressSelectionChanged = false;
            }
        }

        private void ViewModel_SelectionCleared(object sender, EventArgs e)
        {
            SelectionCleared?.Invoke(this, EventArgs.Empty);
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(StatusListViewModel.SelectedAuthor))
            {
                SyncSelectionFromViewModel();
            }
        }

        private void SyncSelectionFromViewModel()
        {
            if (ViewModel == null)
            {
                return;
            }

            _suppressSelectionChanged = true;
            try
            {
                AuthorList.SelectedItem = ViewModel.SelectedAuthor;
            }
            finally
            {
                _suppressSelectionChanged = false;
            }
        }

        private void AuthorList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionChanged || ViewModel == null)
            {
                return;
            }

            var author = AuthorList.SelectedItem as StatusAuthorItem;
            if (author == null)
            {
                return;
            }

            // Same author re-selected (list reload / delayed SelectionChanged) — do not reopen.
            if (ViewModel.SelectedAuthor != null &&
                string.Equals(
                    ViewModel.SelectedAuthor.Jid,
                    author.Jid,
                    System.StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ViewModel.SelectedAuthor = author;
            AuthorSelected?.Invoke(this, new StatusAuthorSelectedEventArgs(author));
        }

        private void MenuButton_Click(object sender, RoutedEventArgs e)
        {
            MenuClicked?.Invoke(this, EventArgs.Empty);
        }
    }
}
