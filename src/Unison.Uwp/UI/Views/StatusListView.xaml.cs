using System;
using System.ComponentModel;
using System.Diagnostics;
using Unison.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Models;
using Unison.Core.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace Unison.Uwp.UI.Views
{
    public sealed class StatusAuthorSelectedEventArgs : EventArgs
    {
        public StatusAuthorItem Author { get; }

        /// <summary>Status to show first; null opens at the oldest.</summary>
        public string StartMessageId { get; }

        public StatusAuthorSelectedEventArgs(StatusAuthorItem author, string startMessageId = null)
        {
            Author = author;
            StartMessageId = startMessageId;
        }
    }

    public sealed partial class StatusListView : UserControl
    {
        private bool _hooked;
        private bool _suppressSelectionChanged;

        public StatusListViewModel ViewModel { get; private set; }

        public event EventHandler<StatusAuthorSelectedEventArgs> AuthorSelected;

        public event EventHandler MenuClicked;

        public event EventHandler SelectionCleared;

        public StatusListView()
        {
            if (App.Services != null)
            {
                ViewModel = App.Services.GetRequiredService<StatusListViewModel>();
                DataContext = ViewModel;
            }

            InitializeComponent();
            MyStatusItem.AddHandler(TappedEvent, new TappedEventHandler(MyStatusRow_Tapped), true);
            Loaded += StatusListView_Loaded;
            Unloaded += StatusListView_Unloaded;
        }

        private void StatusListView_Loaded(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null || _hooked)
            {
                return;
            }

            _hooked = true;
            ViewModel.SelectionCleared += ViewModel_SelectionCleared;
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
            ViewModel.Attach();
            SyncSelectionFromViewModel();
            ApplySelfStatusRow();
        }

        private void ApplySelfStatusRow()
        {
            MyStatusRowChrome.Apply(ViewModel?.SelfStatus, MyStatusRing, MyStatusPlainAvatar, MyStatusCaption, MyStatusTime);
            MyStatusItem.IsSelected = MyStatusRowChrome.IsSelfSelected(ViewModel?.SelectedAuthor, ViewModel?.SelfStatus);
        }

        private void MyStatusRow_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (MyStatusRowChrome.IsWithin(e.OriginalSource, AddStatusButton))
            {
                return;
            }

            StatusAuthorItem self = ViewModel?.SelfStatus;
            TraceSelfMatch("my-status-tap", self);
            if (self == null)
            {
                return;
            }

            e.Handled = true;
            MyStatusItem.IsSelected = false;
            _ = ViewModel.OpenMyStatusAsync();
        }

        private void MyStatusBackButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.CloseMyStatus();
        }

        private void OwnStatusList_ItemClick(object sender, ItemClickEventArgs e)
        {
            StatusAuthorItem self = ViewModel?.SelfStatus;
            if (self == null || !(e.ClickedItem is OwnStatusItem item))
            {
                return;
            }

            ViewModel.SelectedAuthor = self;
            AuthorSelected?.Invoke(this, new StatusAuthorSelectedEventArgs(self, item.MessageId));
        }

        /// <summary>Closes the "My status" page; false when it was not open.</summary>
        public bool TryHandleBack()
        {
            if (ViewModel != null && ViewModel.TryHandleNewStatusBack())
            {
                return true;
            }

            if (ViewModel == null || !ViewModel.IsMyStatusOpen)
            {
                return false;
            }

            ViewModel.CloseMyStatus();
            return true;
        }

        private void TraceSelfMatch(string eventName, StatusAuthorItem author)
        {
            if (ViewModel == null)
            {
                return;
            }

            string details = ViewModel.DescribeSelfMatch(author);
            Debug.WriteLine("[StatusList] " + eventName + " " + details);
            App.Services?.GetService<IRuntimeDiagnostics>()?.Write("status-self", eventName, details);
        }

        private void AddStatusButton_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
        }

        private void StatusListView_Unloaded(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null || !_hooked)
            {
                return;
            }

            _hooked = false;
            ViewModel.SelectionCleared -= ViewModel_SelectionCleared;
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            ViewModel.Detach();
        }

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
            else if (e.PropertyName == nameof(StatusListViewModel.SelfStatus))
            {
                ApplySelfStatusRow();
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
                MyStatusItem.IsSelected = MyStatusRowChrome.IsSelfSelected(ViewModel.SelectedAuthor, ViewModel.SelfStatus);
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

            TraceSelfMatch("list-tap", author);

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
