using System;
using System.ComponentModel;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.ViewModels;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Media.Animation;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// New Conversation overlay: action tiles + message-yourself + A–Z contacts.
    /// Hosts set <see cref="ViewModel"/>; visibility follows <see cref="NewConversationViewModel.IsOpen"/>.
    /// </summary>
    public sealed partial class NewConversationView : UserControl
    {
        public static readonly DependencyProperty ViewModelProperty =
            DependencyProperty.Register(
                nameof(ViewModel),
                typeof(NewConversationViewModel),
                typeof(NewConversationView),
                new PropertyMetadata(null, OnViewModelChanged));

        private readonly CollectionViewSource _groupedCvs = new CollectionViewSource
        {
            IsSourceGrouped = true
        };

        private NewConversationViewModel _hookedVm;
        private bool _backHooked;
        private AlphabetChatGroup _pendingJumpGroup;

        public NewConversationView()
        {
            InitializeComponent();
            Visibility = Visibility.Collapsed;
            Opacity = 1;
            Unloaded += NewConversationView_Unloaded;
        }

        public NewConversationViewModel ViewModel
        {
            get => (NewConversationViewModel)GetValue(ViewModelProperty);
            set => SetValue(ViewModelProperty, value);
        }

        private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as NewConversationView;
            control?.AttachViewModel(e.OldValue as NewConversationViewModel, e.NewValue as NewConversationViewModel);
        }

        private void AttachViewModel(NewConversationViewModel oldVm, NewConversationViewModel newVm)
        {
            if (_hookedVm != null)
            {
                _hookedVm.PropertyChanged -= ViewModel_PropertyChanged;
                _hookedVm = null;
            }

            DataContext = newVm;
            _hookedVm = newVm;
            if (_hookedVm == null)
            {
                BeginClose(animate: false);
                return;
            }

            _hookedVm.PropertyChanged += ViewModel_PropertyChanged;
            if (_hookedVm.IsOpen)
            {
                BeginOpen();
            }
            else
            {
                BeginClose(animate: false);
            }
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null || ViewModel == null)
            {
                return;
            }

            if (string.Equals(e.PropertyName, nameof(NewConversationViewModel.IsOpen), StringComparison.Ordinal))
            {
                if (ViewModel.IsOpen)
                {
                    BeginOpen();
                }
                else
                {
                    BeginClose(animate: true);
                }

                return;
            }

            if (string.Equals(e.PropertyName, nameof(NewConversationViewModel.GroupedContacts), StringComparison.Ordinal))
            {
                BindGroupedSource();
            }

            if (string.Equals(e.PropertyName, nameof(NewConversationViewModel.JumpTiles), StringComparison.Ordinal))
            {
                JumpList.ItemsSource = ViewModel.JumpTiles;
            }
        }

        private void BeginOpen()
        {
            ContactsZoom.IsZoomedInViewActive = true;
            Opacity = 0;
            Visibility = Visibility.Visible;
            HookBack();
            BindGroupedSource();
            JumpList.ItemsSource = ViewModel?.JumpTiles;
            RunOpenFade();
        }

        private void BeginClose(bool animate)
        {
            UnhookBack();
            _pendingJumpGroup = null;
            _groupedCvs.Source = null;
            ContactsList.ItemsSource = null;
            JumpList.ItemsSource = null;
            Opacity = 1;
            Visibility = Visibility.Collapsed;
        }

        private void BindGroupedSource()
        {
            if (ViewModel == null)
            {
                return;
            }

            _groupedCvs.Source = ViewModel.GroupedContacts;
            ContactsList.ItemsSource = _groupedCvs.View;
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
            if (ViewModel == null || !ViewModel.IsOpen)
            {
                return;
            }

            if (!ContactsZoom.IsZoomedInViewActive)
            {
                e.Handled = true;
                ContactsZoom.IsZoomedInViewActive = true;
                return;
            }

            e.Handled = true;
            ViewModel.Close();
        }

        private void NewConversationView_Unloaded(object sender, RoutedEventArgs e)
        {
            UnhookBack();
            if (_hookedVm != null)
            {
                _hookedVm.PropertyChanged -= ViewModel_PropertyChanged;
                _hookedVm = null;
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.Close();
        }

        /// <summary>
        /// Minimal grid: keep 3+2 and scale each square to the column width (capped at 150).
        /// </summary>
        private void OptionsTilesHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (OptionsTilesHost == null ||
                OptionsTilesHost.Visibility != Visibility.Visible ||
                e.NewSize.Width <= 0)
            {
                return;
            }

            // AttachTile / SelfTile Margin="5" → 10px horizontal chrome per cell.
            const double tileMargin = 5;
            const double maxSide = 150;
            const int columns = 3;
            double side = Math.Floor((e.NewSize.Width / columns) - (tileMargin * 2));
            if (side < 1)
            {
                return;
            }

            if (side > maxSide)
            {
                side = maxSide;
            }

            ApplyMinimalTileSide(TileNewGroup, side);
            ApplyMinimalTileSide(TileNewContact, side);
            ApplyMinimalTileSide(TileNewCommunity, side);
            ApplyMinimalTileSide(TilePhoneNumber, side);
            ApplyMinimalTileSide(SelfTile, side);
        }

        private static void ApplyMinimalTileSide(FrameworkElement tile, double side)
        {
            if (tile == null)
            {
                return;
            }

            tile.Width = side;
            tile.Height = side;
        }

        private void ContactsList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var chat = e.ClickedItem as ChatItem;
            if (chat == null || ViewModel?.SelectContactCommand == null)
            {
                return;
            }

            if (ViewModel.SelectContactCommand.CanExecute(chat))
            {
                ViewModel.SelectContactCommand.Execute(chat);
            }
        }

        private void JumpList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var tile = e.ClickedItem as AlphabetJumpTile;
            if (tile == null || !tile.IsEnabled || tile.Group == null || tile.Group.Count == 0)
            {
                return;
            }

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

            ScheduleScrollToPendingGroup();
        }

        private void ScheduleScrollToPendingGroup()
        {
            AlphabetChatGroup group = _pendingJumpGroup;
            if (group == null || group.Count == 0)
            {
                return;
            }

            ChatItem first = group[0];
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                try
                {
                    ContactsList.ScrollIntoView(first, ScrollIntoViewAlignment.Leading);
                }
                catch
                {
                }

                _pendingJumpGroup = null;
            });
        }
    }
}
