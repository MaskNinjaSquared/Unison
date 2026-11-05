using System;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Contracts;
using Unison.Core.Models;
using Unison.Core.ViewModels;
using Unison.Uwp.UI.Controls;
using Unison.Uwp.UI.Views;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media.Animation;

namespace Unison.Uwp.Shell.WhatsAppLegacy.Views
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
        private ShellViewModel _shell;
        private Storyboard _newStatusDockStoryboard;
        private bool _newStatusDockOpen;

        /// <summary>Same slide and fade as the chat attachment bar.</summary>
        private static readonly TimeSpan NewStatusDockDuration = TimeSpan.FromMilliseconds(220);

        /// <summary>Attachment-bar tile ceiling (Windows Phone 8 size), three across.</summary>
        private const double NewStatusTileMaxSize = 150;

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
            SizeChanged += (_, e) => ApplyNewStatusTileSize(e.NewSize.Width);
            MyStatusItem.AddHandler(TappedEvent, new TappedEventHandler(MyStatusRow_Tapped), true);
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

        private void AddStatusButton_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
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
            else if (e.PropertyName == nameof(StatusListViewModel.SelfStatus))
            {
                ApplySelfStatusRow();
            }
            else if (e.PropertyName == nameof(StatusListViewModel.IsNewStatusPickerOpen))
            {
                SetNewStatusDockOpen(ViewModel.IsNewStatusPickerOpen);
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

        private void NewStatusScrim_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
            ViewModel?.CloseNewStatusPicker();
        }

        /// <summary>Scrim fades, dock slides its own measured height (attachment bar pattern).</summary>
        private void SetNewStatusDockOpen(bool open)
        {
            if (_newStatusDockOpen == open)
            {
                return;
            }

            _newStatusDockOpen = open;
            _newStatusDockStoryboard?.Stop();

            if (open)
            {
                NewStatusScrim.Visibility = Visibility.Visible;
                NewStatusDock.Visibility = Visibility.Visible;
                ApplyNewStatusTileSize(ActualWidth);
                NewStatusDock.UpdateLayout();
            }

            double distance = NewStatusDock.ActualHeight > 0 ? NewStatusDock.ActualHeight : 320;
            var easing = new CubicEase { EasingMode = open ? EasingMode.EaseOut : EasingMode.EaseIn };
            var slide = new DoubleAnimation
            {
                From = open ? distance : 0,
                To = open ? 0 : distance,
                Duration = new Duration(NewStatusDockDuration),
                EasingFunction = easing
            };
            Storyboard.SetTarget(slide, NewStatusDock);
            Storyboard.SetTargetProperty(slide, "(UIElement.RenderTransform).(TranslateTransform.Y)");

            var fade = new DoubleAnimation
            {
                To = open ? 1 : 0,
                Duration = new Duration(NewStatusDockDuration)
            };
            Storyboard.SetTarget(fade, NewStatusScrim);
            Storyboard.SetTargetProperty(fade, "Opacity");

            var storyboard = new Storyboard();
            storyboard.Children.Add(slide);
            storyboard.Children.Add(fade);
            if (!open)
            {
                storyboard.Completed += NewStatusDockHide_Completed;
            }

            _newStatusDockStoryboard = storyboard;
            storyboard.Begin();
        }

        private void NewStatusDockHide_Completed(object sender, object e)
        {
            // A reopen during the slide out leaves this queued; it must not collapse the dock.
            if (_newStatusDockOpen)
            {
                return;
            }

            NewStatusScrim.Visibility = Visibility.Collapsed;
            NewStatusDock.Visibility = Visibility.Collapsed;
        }

        /// <summary>Tiles sized as in the attachment bar (three across), so both docks match.</summary>
        private void ApplyNewStatusTileSize(double width)
        {
            if (NewStatusTilesGrid == null || width <= 0)
            {
                return;
            }

            double size = Math.Min(NewStatusTileMaxSize, (width - 10 - (3 * 10)) / 3);
            if (size <= 0)
            {
                return;
            }

            foreach (UIElement child in NewStatusTilesGrid.Children)
            {
                if (child is AttachTile tile)
                {
                    tile.Width = size;
                }
            }
        }

        private void MenuButton_Click(object sender, RoutedEventArgs e)
        {
            MenuClicked?.Invoke(this, EventArgs.Empty);
        }

        private OwnStatusItem _ownStatusFlyoutItem;

        /// <summary>
        /// MenuFlyout pops out of the item tree — capture the row so Save Click is not Command-bound
        /// (CommandParameter is flaky on older Mobile UWP builds).
        /// </summary>
        private void OwnStatusMoreFlyout_Opening(object sender, object e)
        {
            var flyout = sender as MenuFlyout;
            var target = flyout?.Target as FrameworkElement;
            _ownStatusFlyoutItem = target?.DataContext as OwnStatusItem;
        }

        private void OwnStatusSave_Click(object sender, RoutedEventArgs e)
        {
            OwnStatusItem item = _ownStatusFlyoutItem
                ?? (sender as FrameworkElement)?.DataContext as OwnStatusItem;
            if (item == null || ViewModel == null)
            {
                Debug.WriteLine("[StatusList] OwnStatusSave: no item");
                return;
            }

            _ = ViewModel.SaveOwnStatusAsync(item);
        }
    }
}
