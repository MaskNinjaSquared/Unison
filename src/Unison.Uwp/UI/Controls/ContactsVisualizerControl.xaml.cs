using System;
using System.Collections;
using System.Collections.Generic;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Media.Animation;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Generic contact/chat picker overlay. Hosts pass a title, collection, and click command —
    /// open presentation (compact rows, deferred bind, fade) comes from the active shell theme strategy.
    /// Always groups A–Z for SemanticZoom (no category) so jump letters match Favourites.
    /// </summary>
    public sealed partial class ContactsVisualizerControl : UserControl
    {
        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(
                nameof(Title),
                typeof(string),
                typeof(ContactsVisualizerControl),
                new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty ItemsSourceProperty =
            DependencyProperty.Register(
                nameof(ItemsSource),
                typeof(IEnumerable),
                typeof(ContactsVisualizerControl),
                new PropertyMetadata(null, OnItemsSourceChanged));

        public static readonly DependencyProperty ItemClickCommandProperty =
            DependencyProperty.Register(
                nameof(ItemClickCommand),
                typeof(ICommand),
                typeof(ContactsVisualizerControl),
                new PropertyMetadata(null));

        public static readonly DependencyProperty BackCommandProperty =
            DependencyProperty.Register(
                nameof(BackCommand),
                typeof(ICommand),
                typeof(ContactsVisualizerControl),
                new PropertyMetadata(null));

        public static readonly DependencyProperty IsOpenProperty =
            DependencyProperty.Register(
                nameof(IsOpen),
                typeof(bool),
                typeof(ContactsVisualizerControl),
                new PropertyMetadata(false, OnIsOpenChanged));

        private readonly CollectionViewSource _groupedCvs = new CollectionViewSource
        {
            IsSourceGrouped = true
        };

        private bool _backHooked;
        private bool _openInProgress;
        private bool _clickBusy;
        private ModernProgressRing _rowBusyRing;
        private UIElement _rowBusyAvatar;
        private UIElement _rowBusyName;
        private AlphabetChatGroup _pendingJumpGroup;

        public ContactsVisualizerControl()
        {
            InitializeComponent();
            Visibility = Visibility.Collapsed;
            Opacity = 1;
            Unloaded += ContactsVisualizerControl_Unloaded;
        }

        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        public IEnumerable ItemsSource
        {
            get => (IEnumerable)GetValue(ItemsSourceProperty);
            set => SetValue(ItemsSourceProperty, value);
        }

        public ICommand ItemClickCommand
        {
            get => (ICommand)GetValue(ItemClickCommandProperty);
            set => SetValue(ItemClickCommandProperty, value);
        }

        public ICommand BackCommand
        {
            get => (ICommand)GetValue(BackCommandProperty);
            set => SetValue(BackCommandProperty, value);
        }

        public bool IsOpen
        {
            get => (bool)GetValue(IsOpenProperty);
            set => SetValue(IsOpenProperty, value);
        }

        private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as ContactsVisualizerControl;
            if (control == null || !control.IsOpen)
            {
                return;
            }

            // Share Target cold open often swaps ItemsSource while BeginOpen's deferred
            // Low bind is still in flight (_openInProgress). Still push the latest DP so
            // the deferred callback and the ListView both see the filled catalog.
            control.BindGroupedSource(e.NewValue as IEnumerable);
        }

        private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as ContactsVisualizerControl;
            if (control == null)
            {
                return;
            }

            if ((bool)e.NewValue)
            {
                control.BeginOpen();
            }
            else
            {
                control.BeginClose();
            }
        }

        private void BeginOpen()
        {
            _openInProgress = true;
            _clickBusy = false;
            HideClickBusy();
            ApplyPickerTemplate();
            ContactsZoom.IsZoomedInViewActive = true;

            bool animate = ResolveTheme()?.ContactsPickerAnimatesOpen ?? true;
            bool defer = ResolveTheme()?.ContactsPickerDefersItemsUntilOpen ?? true;

            if (animate)
            {
                Opacity = 0;
            }

            Visibility = Visibility.Visible;
            HookBack();

            if (defer)
            {
                // Realize rows after the overlay surface is on screen (Mobile jank fix).
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                {
                    if (!IsOpen)
                    {
                        _openInProgress = false;
                        return;
                    }

                    BindGroupedSource(ItemsSource);
                    _openInProgress = false;
                    if (animate)
                    {
                        RunOpenFade();
                    }
                    else
                    {
                        Opacity = 1;
                    }
                });
                return;
            }

            BindGroupedSource(ItemsSource);
            _openInProgress = false;
            if (animate)
            {
                RunOpenFade();
            }
            else
            {
                Opacity = 1;
            }
        }

        private void BeginClose()
        {
            UnhookBack();
            _pendingJumpGroup = null;
            _clickBusy = false;
            HideClickBusy();
            _groupedCvs.Source = null;
            ItemsList.ItemsSource = null;
            JumpList.ItemsSource = null;
            Opacity = 1;
            Visibility = Visibility.Collapsed;
            _openInProgress = false;
        }

        private void BindGroupedSource(IEnumerable source)
        {
            List<ChatItem> chats = EnumerateChats(source);
            var groups = AlphabetChatGroups.Build(
                chats,
                c => c.GetNameResolved(null) ?? c.Name ?? c.JID ?? string.Empty);
            _groupedCvs.Source = groups;
            ItemsList.ItemsSource = _groupedCvs.View;
            JumpList.ItemsSource = AlphabetChatGroups.BuildJumpTiles(groups);
        }

        private static List<ChatItem> EnumerateChats(IEnumerable source)
        {
            var list = new List<ChatItem>();
            if (source == null)
            {
                return list;
            }

            foreach (object item in source)
            {
                var chat = item as ChatItem;
                if (chat != null)
                {
                    list.Add(chat);
                }
            }

            return list;
        }

        private void ApplyPickerTemplate()
        {
            bool compact = ResolveTheme()?.ContactsPickerUsesCompactRows ?? true;
            if (compact)
            {
                ItemsList.ItemTemplate = (DataTemplate)Resources["ContactsPickerCompactTemplate"];
                return;
            }

            if (Resources.ContainsKey("ChatItemTemplate"))
            {
                ItemsList.ItemTemplate = Resources["ChatItemTemplate"] as DataTemplate;
            }
        }

        private void RunOpenFade()
        {
            var anim = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(160),
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(anim, this);
            Storyboard.SetTargetProperty(anim, "Opacity");
            var sb = new Storyboard();
            sb.Children.Add(anim);
            sb.Begin();
        }

        private static IShellThemeService ResolveTheme()
        {
            try
            {
                return App.Services?.GetService<IShellThemeService>();
            }
            catch
            {
                return null;
            }
        }

        private void HookBack()
        {
            if (_backHooked)
            {
                return;
            }

            SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
            _backHooked = true;
        }

        private void UnhookBack()
        {
            if (!_backHooked)
            {
                return;
            }

            SystemNavigationManager.GetForCurrentView().BackRequested -= OnBackRequested;
            _backHooked = false;
        }

        private void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            if (!IsOpen)
            {
                return;
            }

            if (_clickBusy)
            {
                e.Handled = true;
                return;
            }

            if (!ContactsZoom.IsZoomedInViewActive)
            {
                e.Handled = true;
                ContactsZoom.IsZoomedInViewActive = true;
                return;
            }

            e.Handled = true;
            RequestClose();
        }

        private void ContactsVisualizerControl_Unloaded(object sender, RoutedEventArgs e)
        {
            UnhookBack();
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (_clickBusy)
            {
                return;
            }

            if (!ContactsZoom.IsZoomedInViewActive)
            {
                ContactsZoom.IsZoomedInViewActive = true;
                return;
            }

            RequestClose();
        }

        private void RequestClose()
        {
            if (BackCommand != null && BackCommand.CanExecute(null))
            {
                BackCommand.Execute(null);
                return;
            }

            IsOpen = false;
        }

        private void ShowClickBusy(ChatItem chat)
        {
            // Mandatory for every picker command: row ring (compact) + scrim so the tap
            // never looks dead. Command runs only on the next dispatcher turn.
            ShowRowClickBusy(chat);
            ClickBusyOverlay.Visibility = Visibility.Visible;
            ClickBusyRing.IsActive = true;
        }

        private void HideClickBusy()
        {
            HideRowClickBusy();
            ClickBusyRing.IsActive = false;
            ClickBusyOverlay.Visibility = Visibility.Collapsed;
        }

        private void ShowRowClickBusy(ChatItem chat)
        {
            HideRowClickBusy();
            if (chat == null)
            {
                return;
            }

            var container = ItemsList.ContainerFromItem(chat) as ListViewItem;
            var root = container?.ContentTemplateRoot as FrameworkElement;
            if (root == null)
            {
                return;
            }

            _rowBusyRing = root.FindName("RowClickBusyRing") as ModernProgressRing;
            _rowBusyAvatar = root.FindName("RowAvatar") as UIElement;
            _rowBusyName = root.FindName("RowName") as UIElement;
            if (_rowBusyAvatar != null)
            {
                _rowBusyAvatar.Opacity = 0.25;
            }

            if (_rowBusyName != null)
            {
                _rowBusyName.Opacity = 0.25;
            }

            if (_rowBusyRing != null)
            {
                _rowBusyRing.Visibility = Visibility.Visible;
                _rowBusyRing.IsActive = true;
            }
        }

        private void HideRowClickBusy()
        {
            if (_rowBusyRing != null)
            {
                _rowBusyRing.IsActive = false;
                _rowBusyRing.Visibility = Visibility.Collapsed;
                _rowBusyRing = null;
            }

            if (_rowBusyAvatar != null)
            {
                _rowBusyAvatar.Opacity = 1;
                _rowBusyAvatar = null;
            }

            if (_rowBusyName != null)
            {
                _rowBusyName.Opacity = 1;
                _rowBusyName = null;
            }
        }

        private void ItemsList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var chat = e.ClickedItem as ChatItem;
            if (_clickBusy || chat == null || ItemClickCommand == null)
            {
                return;
            }

            if (!ItemClickCommand.CanExecute(chat))
            {
                return;
            }

            // Contract: paint loading on the row (+ scrim) this turn, then run the host
            // command (Share / Favourite / Forward stub / …) on the next. Never invert.
            _clickBusy = true;
            ShowClickBusy(chat);
            ChatItem selected = chat;
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                try
                {
                    if (ItemClickCommand != null && ItemClickCommand.CanExecute(selected))
                    {
                        ItemClickCommand.Execute(selected);
                    }
                }
                finally
                {
                    // Picker usually closes via IsOpen; if the command left it open, clear busy.
                    if (IsOpen)
                    {
                        _clickBusy = false;
                        HideClickBusy();
                    }
                }
            });
        }

        private void JumpList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var tile = e.ClickedItem as AlphabetJumpTile;
            if (tile == null || !tile.IsEnabled || tile.Group == null || tile.Group.Count == 0)
            {
                return;
            }

            // ScrollIntoView(group) fails inside SemanticZoom: the zoomed-in list knows ChatItem
            // rows (and MakeVisible), not the AlphabetChatGroup bucket. Defer until zoom settles.
            _pendingJumpGroup = tile.Group;
            if (!ContactsZoom.IsZoomedInViewActive)
            {
                ContactsZoom.IsZoomedInViewActive = true;
                return;
            }

            ScheduleScrollToPendingGroup();
        }

        private void ContactsZoom_ViewChangeStarted(object sender, SemanticZoomViewChangedEventArgs e)
        {
            // Pinch / programmatic zoom-in: map the jump letter onto a real list row.
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

        private void ContactsZoom_ViewChangeCompleted(object sender, SemanticZoomViewChangedEventArgs e)
        {
            if (e == null || e.IsSourceZoomedInView || _pendingJumpGroup == null)
            {
                return;
            }

            // After zoom settles, MakeVisible/ScrollIntoView can find the container.
            ScheduleScrollToPendingGroup();
        }

        private void ScheduleScrollToPendingGroup()
        {
            AlphabetChatGroup group = _pendingJumpGroup;
            if (group == null || group.Count == 0)
            {
                return;
            }

            // Low: after Normal UI from the zoom transition has drained.
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            {
                if (!IsOpen || !ReferenceEquals(_pendingJumpGroup, group))
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
                ItemsList.UpdateLayout();
                // Docs: use MakeVisible when the ListView is a SemanticZoom view.
                ItemsList.MakeVisible(new SemanticZoomLocation { Item = first });
                ItemsList.ScrollIntoView(first, ScrollIntoViewAlignment.Leading);
            }
            catch
            {
                // Container may still be unrealized; zoom-in alone is still useful.
            }
        }
    }
}
