using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Factories;
using Unison.Core.Helpers;
using Unison.Core.Mappers;
using Unison.Core.Models;
using Unison.Core.ViewModels;
using Unison.Uwp.Client;
using Unison.Uwp.Helpers;
using Unison.Uwp.Services;
using Unison.Uwp.Services.WhatsApp;
using Windows.Foundation;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media.Animation;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Linq;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Views
{
    public sealed partial class ChatDetailView : UserControl
    {
        private ChatItem _activeChat;
        private ObservableCollection<ChatMessageViewModel> _messages;
        private readonly bool _isWindowsMobile;
        public event EventHandler BackRequested;
        private Storyboard _chatDetailInfoSlideStoryboard;
        private bool _chatDetailInfoPaneShown;

        /// <summary>
        /// DI ViewModel owns composer, pin, audio prepare, presence watch, and timeline VMs.
        /// List chrome / MediaElement / Storyboards stay in code-behind.
        /// Loaded â†’ InitializeAsync; Unloaded â†’ UninitializeAsync.
        /// </summary>
        public ChatDetailViewModel ViewModel { get; private set; }

        private readonly IStringResources _strings;
        private readonly IChatMessageVmFactory _messageFactory;
        private readonly IVoicePlaybackRoutingService _voiceRouting;

        /// <summary>
        /// Chat state the timeline needs and the view model does not hold: canonical JIDs, older
        /// pages of history, and which conversation is on screen for the notification suppressor.
        /// Through the contract rather than the class, so this view survives the client behind it
        /// being replaced.
        /// </summary>
        private IWhatsAppService WhatsApp => App.GetWhatsAppService();

        /// <summary>
        /// Where message-shaped news comes from. The client above is still here for the UWP-only
        /// helpers, but anything the domain has a word for is asked of the facade.
        /// </summary>
        private IMessageService MessagesFacade =>
            App.Services?.GetService(typeof(IMessageService)) as IMessageService;

        public bool HasActiveChat => ActiveChatGrid.Visibility == Visibility.Visible;

        private ScrollViewer _scrollViewer;
        private bool _isSyncingFromService = false;
        private bool _syncRequestedAgain = false;
        private DateTime _suppressLoadMoreUntilUtc = DateTime.MinValue;
        /// <summary>Throttle visible group-author avatar hydrate while scrolling.</summary>
        private DateTime _lastVisibleAvatarRequestUtc = DateTime.MinValue;
        private bool _messageListCacheLengthApplied;
        /// <summary>After an outgoing send, keep snapping to the true bottom until the bubble lands.</summary>
        private DateTime _stickToBottomUntilUtc = DateTime.MinValue;
        private CancellationTokenSource _chatLoadCts;
        private bool _serviceEventsAttached;
        private bool _viewModelEventsAttached;
        private ChatMessageViewModel _displayedPinnedMessage;
        private List<ChatMessageViewModel> _activePinnedMessages = new List<ChatMessageViewModel>();
        private int _displayedPinnedIndex;
        private ChatMessage _playingAudioMessage;
        private ChatMessageViewModel _playingAudioVm;
        private MediaPlayer _audioMediaPlayer;
        private DispatcherTimer _audioPositionTimer;
        private DispatcherTimer _dateSeparatorTimer;
        private DateTime _dateSeparatorTimerDay;
        private bool _cancelRecordingAnimating;

        /// <summary>Transient target of quote-tap flash; cleared after a short delay.</summary>
        private ChatMessageViewModel _highlightedMessage;
        private int _highlightGeneration;

        /// <summary>Cancels in-flight Storyboard sequences when presence watch restarts.</summary>
        private CancellationTokenSource _presenceAnimationCts;

        /// <summary>Adaptive layout group, watched to lower the attachment bar when it widens.</summary>
        private VisualStateGroup _layoutStates;

        /// <summary>
        /// Whether the narrow-layout attachment bar is up. Kept here rather than on the view model
        /// because it is the position of a menu and nothing else - the wide layout does the same
        /// job with a flyout that no one outside the markup knows about, and this should cost the
        /// same. The six commands it invokes are the part that belongs to the view model.
        /// </summary>
        private bool _attachMenuOpen;

        /// <summary>The slide in flight, kept so a fast second tap can cut the first one short.</summary>
        private Storyboard _attachMenuStoryboard;

        public ChatDetailView()
        {
            _strings = App.Services?.GetService<IStringResources>();
            _messageFactory = App.Services?.GetService<IChatMessageVmFactory>() ?? new ChatMessageVmFactory();
            _voiceRouting = App.Services?.GetService<IVoicePlaybackRoutingService>();

            if (App.Services != null)
            {
                ViewModel = App.Services.GetRequiredService<ChatDetailViewModel>();
                DataContext = ViewModel;
                _isWindowsMobile = App.Services.GetRequiredService<ISystemInfoProvider>().IsMobile();
            }

            _messages = ViewModel != null
                ? ViewModel.Messages
                : new ObservableCollection<ChatMessageViewModel>();

            this.InitializeComponent();
            MessageListView.ItemsSource = _messages;
            MessageListView.Loaded += MessageListView_Loaded;
            this.Loaded += ChatDetailView_Loaded;
            this.Unloaded += ChatDetailView_Unloaded;
            this.SizeChanged += ChatDetailView_SizeChanged;
        }

        private void ChatDetailView_Loaded(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
            {
                AttachViewModelEvents();
                _ = ViewModel.InitializeAsync();
            }

            var messages = MessagesFacade;
            if (!_serviceEventsAttached && messages != null)
            {
                messages.ChatMessagesChanged += MessageService_ChatMessagesChanged;
                _serviceEventsAttached = true;
            }

            ApplyChatDetailInfoPane();
            HookLayoutStateChanges();
            StartDateSeparatorTimer();
        }

        /// <summary>
        /// ViewModel events live on the Loaded/Unloaded pair, not on the constructor. The view model
        /// is transient but a live <see cref="ChatItem"/> keeps it subscribed, so a handler pointing
        /// back here would keep the whole visual tree alive after navigating away.
        /// </summary>
        private void AttachViewModelEvents()
        {
            if (_viewModelEventsAttached || ViewModel == null)
            {
                return;
            }

            _viewModelEventsAttached = true;
            ViewModel.BackRequested += ViewModel_BackRequested;
            ViewModel.MessageSent += ViewModel_MessageSent;
            ViewModel.MessagePinnedChanged += ViewModel_MessagePinnedChanged;
            ViewModel.PresenceAnimationRequested += ViewModel_PresenceAnimationRequested;
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        }

        private void DetachViewModelEvents()
        {
            if (!_viewModelEventsAttached || ViewModel == null)
            {
                return;
            }

            _viewModelEventsAttached = false;
            ViewModel.BackRequested -= ViewModel_BackRequested;
            ViewModel.MessageSent -= ViewModel_MessageSent;
            ViewModel.MessagePinnedChanged -= ViewModel_MessagePinnedChanged;
            ViewModel.PresenceAnimationRequested -= ViewModel_PresenceAnimationRequested;
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        }

        private void ViewModel_BackRequested(object sender, EventArgs e)
        {
            BackRequested?.Invoke(this, e);
        }

        private void ViewModel_MessageSent(object sender, EventArgs e)
        {
            _stickToBottomUntilUtc = DateTime.UtcNow.AddSeconds(2);
            _ = StickScrollToBottomAfterSendAsync();
        }

        private void ViewModel_MessagePinnedChanged(object sender, EventArgs e)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, UpdatePinnedBanner);
        }

        /// <summary>
        /// Watches the adaptive layout so the attachment bar can be lowered when the window grows
        /// past the point where the wide layout's flyout takes over.
        /// </summary>
        /// <remarks>
        /// Without this, widening the window while the bar is up leaves it there with no way to
        /// dismiss it: the button that raised it has been swapped for the flyout one, and the
        /// flyout's own dismissal has nothing to do with the bar. Read off the state group rather
        /// than re-testing the width here, so the breakpoint stays declared in one place.
        /// </remarks>
        private void HookLayoutStateChanges()
        {
            if (_layoutStates != null || ChatDetailGrid == null)
            {
                return;
            }

            foreach (var group in VisualStateManager.GetVisualStateGroups(ChatDetailGrid))
            {
                if (group.Name != "LayoutStates")
                {
                    continue;
                }

                _layoutStates = group;
                group.CurrentStateChanged += LayoutStates_CurrentStateChanged;
                break;
            }
        }

        private void LayoutStates_CurrentStateChanged(object sender, VisualStateChangedEventArgs e)
        {
            if (e.NewState != null && e.NewState.Name != "Minimal")
            {
                SetAttachMenuOpen(false);
            }
        }

        private void AttachBarButton_Click(object sender, RoutedEventArgs e) =>
            SetAttachMenuOpen(!_attachMenuOpen);

        private void AttachMenuScrim_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
            SetAttachMenuOpen(false);
        }

        /// <summary>Choosing an option dismisses the bar, as a flyout item would.</summary>
        private void AttachTile_Invoked(object sender, RoutedEventArgs e) => SetAttachMenuOpen(false);

        private void AttachMenuCloseButton_Click(object sender, RoutedEventArgs e) =>
            SetAttachMenuOpen(false);

        /// <summary>
        /// Raises or lowers the attachment bar. The dimming behind it and the composer trading
        /// places with it are the AttachMenuStates storyboards in the markup; the slide is here,
        /// because it is the only part that has to be measured first.
        /// </summary>
        /// <remarks>
        /// Focus is pushed back to the clip on the way up. Without it the caret stays in the
        /// message box that is no longer on screen, and on a phone that means the soft keyboard
        /// sitting over the bar the user just opened.
        /// </remarks>
        private void SetAttachMenuOpen(bool open)
        {
            if (_attachMenuOpen == open || AttachMenuBar == null)
            {
                return;
            }

            _attachMenuOpen = open;
            VisualStateManager.GoToState(this, open ? "AttachMenuOpen" : "AttachMenuClosed", true);

            if (_attachMenuStoryboard != null)
            {
                _attachMenuStoryboard.Stop();
                _attachMenuStoryboard = null;
            }

            if (open)
            {
                // Shown before it is measured, and measured before it is animated: the distance to
                // travel is the bar's own height, and a collapsed element does not have one. The
                // cap goes on in between, for the same reason - the heading and strip it discounts
                // have no height until the bar is up.
                AttachMenuBar.Visibility = Visibility.Visible;
                ApplyAttachTileSize(ActualWidth);
                AttachMenuBar.UpdateLayout();
                ApplyAttachMenuHeightCap(ActualHeight);
                AttachMenuBar.UpdateLayout();
                AttachBarButton.Focus(FocusState.Programmatic);
            }

            double distance = AttachMenuBar.ActualHeight;
            if (distance <= 0)
            {
                distance = 320;
            }

            _attachMenuStoryboard = BuildAttachMenuSlide(open, distance);
            _attachMenuStoryboard.Begin();
            UpdateScrollToBottomButton();
        }

        /// <summary>
        /// Builds the slide, in code because the distance is the bar's measured height and only
        /// exists once the tiles have wrapped into rows at the current window width.
        /// </summary>
        private Storyboard BuildAttachMenuSlide(bool open, double distance)
        {
            var slide = new DoubleAnimation
            {
                From = open ? distance : 0,
                To = open ? 0 : distance,
                Duration = new Duration(TimeSpan.FromMilliseconds(220)),
                EasingFunction = new CubicEase { EasingMode = open ? EasingMode.EaseOut : EasingMode.EaseIn }
            };
            Storyboard.SetTarget(slide, AttachMenuBar);
            Storyboard.SetTargetProperty(slide, "(UIElement.RenderTransform).(TranslateTransform.Y)");

            var storyboard = new Storyboard();
            storyboard.Children.Add(slide);

            if (!open)
            {
                storyboard.Completed += AttachMenuHide_Completed;
            }

            return storyboard;
        }

        private void AttachMenuHide_Completed(object sender, object e)
        {
            // Guarded: a reopen during the slide out leaves this queued, and letting it run would
            // collapse the bar that is on its way back up.
            if (_attachMenuOpen)
            {
                return;
            }

            AttachMenuBar.Visibility = Visibility.Collapsed;
        }

        private void ChatDetailView_Unloaded(object sender, RoutedEventArgs e)
        {
            var messages = MessagesFacade;
            if (_serviceEventsAttached && messages != null)
            {
                messages.ChatMessagesChanged -= MessageService_ChatMessagesChanged;
                _serviceEventsAttached = false;
            }

            _chatLoadCts?.Cancel();
            _chatLoadCts?.Dispose();
            _chatLoadCts = null;
            CancelPresenceAnimation();
            ClearMessageHighlight();
            TryCloseVideoViewer();
            TryCloseImageViewer();
            StopAudioPlayback();
            StopDateSeparatorTimer();
            _voiceRouting?.DetachPlayer();
            if (ViewModel != null)
            {
                DetachViewModelEvents();
                ViewModel.CloseChatDetailInfo();
                ViewModel.StopPresenceWatch();
                _ = ViewModel.UninitializeAsync();
            }
            WhatsApp.SetActiveChatJid(null);

            if (_activeChat != null)
            {
                _activeChat.PropertyChanged -= ActiveChat_PropertyChanged;
            }

            // Solta tambem o handler de rolagem, senao ele sobrevive a navegacao.
            if (_scrollViewer != null)
            {
                _scrollViewer.ViewChanged -= ScrollViewer_ViewChanged;
            }
        }

        private void ChatDetailView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (ViewModel?.IsChatDetailInfoOpen == true)
            {
                ApplyChatDetailInfoPane();
            }

            ApplyAttachTileSize(e.NewSize.Width);
            ApplyAttachMenuHeightCap(e.NewSize.Height);
            UpdateScrollToBottomButton();
        }

        /// <summary>Largest a tile gets: the size Whatsapp drew them at on Windows Phone 8.</summary>
        private const double AttachTileMaxSize = 150;

        /// <summary>
        /// Sizes the attachment tiles so three fit across the window, up to
        /// <see cref="AttachTileMaxSize"/>.
        /// </summary>
        /// <remarks>
        /// Measured here rather than left to star columns because a column wider than a tile will
        /// grow gives the surplus to the gap between them. Handing the tiles the width instead
        /// means the gap stays at their margins and the surplus ends up outside the block, which
        /// the grid then centres.
        /// </remarks>
        private void ApplyAttachTileSize(double barWidth)
        {
            if (AttachMenuTilesGrid == null || barWidth <= 0)
            {
                return;
            }

            // Off the top: the scroller's padding, then 10px of margin around each of the three.
            double usable = barWidth - 10 - (3 * 10);
            double size = Math.Min(AttachTileMaxSize, usable / 3);
            if (size <= 0)
            {
                return;
            }

            foreach (UIElement child in AttachMenuTilesGrid.Children)
            {
                var tile = child as Unison.Uwp.UI.Controls.AttachTile;
                if (tile != null)
                {
                    tile.Width = size;
                }
            }
        }

        /// <summary>
        /// Ceiling on how much of the chat the attachment bar may take, past which the tiles
        /// scroll.
        /// </summary>
        /// <remarks>
        /// Sizing the tiles off the width already keeps them to two rows and around half the
        /// screen upright, so this never comes into play there. It is for landscape, where the
        /// window stays wide enough to hand the tiles a size the height cannot afford.
        /// </remarks>
        private const double AttachMenuMaxHeightRatio = 0.65;

        private void ApplyAttachMenuHeightCap(double surfaceHeight)
        {
            if (AttachMenuTilesScroll == null || surfaceHeight <= 0)
            {
                return;
            }

            // The heading and the chevron strip come off the top of the allowance: they are the
            // part that must stay on screen for the bar to be usable at all.
            double chrome = AttachMenuTitle.ActualHeight + AttachMenuCloseStrip.ActualHeight;
            double available = (surfaceHeight * AttachMenuMaxHeightRatio) - chrome;

            AttachMenuTilesScroll.MaxHeight = available > 0 ? available : 0;
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ChatDetailViewModel.IsChatDetailInfoOpen) ||
                e.PropertyName == nameof(ChatDetailViewModel.ChatDetailInfo))
            {
                ApplyChatDetailInfoPane();
            }

            if (e.PropertyName == nameof(ChatDetailViewModel.IsRecording) ||
                e.PropertyName == nameof(ChatDetailViewModel.IsGroupLockedForMessages))
            {
                UpdateScrollToBottomButton();
            }

            if (e.PropertyName == nameof(ChatDetailViewModel.IsTimelineBusy) ||
                e.PropertyName == nameof(ChatDetailViewModel.IsLoadingMessages) ||
                e.PropertyName == nameof(ChatDetailViewModel.IsLoadingMore))
            {
                ApplyTimelineScrollLock();
            }
        }

        /// <summary>
        /// While the timeline is loading (open or load-more), freeze vertical scroll so Mobile
        /// does not stack ViewChanged / layout thrash on top of VM materialization.
        /// </summary>
        private void ApplyTimelineScrollLock()
        {
            bool busy = ViewModel?.IsTimelineBusy == true;
            EnsureMessageListScrollViewer();

            if (MessageListView != null)
            {
                ScrollViewer.SetVerticalScrollMode(
                    MessageListView,
                    busy ? ScrollMode.Disabled : ScrollMode.Auto);
                ScrollViewer.SetVerticalScrollBarVisibility(
                    MessageListView,
                    busy ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
            }

            if (_scrollViewer != null)
            {
                _scrollViewer.VerticalScrollMode = busy ? ScrollMode.Disabled : ScrollMode.Auto;
                _scrollViewer.VerticalScrollBarVisibility =
                    busy ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            }
        }

        /// <summary>
        /// Minimal: info covers the chat. Extended+: fixed 400px column on the right.
        /// Opening slides the pane in from the right; SizeChanged only reflows columns.
        /// </summary>
        private void ApplyChatDetailInfoPane()
        {
            if (ChatDetailInfoPaneHost == null || ChatColumn == null || InfoColumn == null)
            {
                return;
            }

            bool open = ViewModel?.IsChatDetailInfoOpen == true;
            var info = ViewModel?.ChatDetailInfo;
            bool member = info != null && info.IsGroupMember;

            if (ChatDetailInfoPanel != null)
            {
                ChatDetailInfoPanel.InfoViewModel = open && !member ? info : null;
                ChatDetailInfoPanel.Visibility =
                    open && !member ? Visibility.Visible : Visibility.Collapsed;
            }

            if (ChatDetailMemberInfoPanel != null)
            {
                ChatDetailMemberInfoPanel.InfoViewModel = open && member ? info : null;
                ChatDetailMemberInfoPanel.Visibility =
                    open && member ? Visibility.Visible : Visibility.Collapsed;
            }

            if (open)
            {
                ApplyChatDetailInfoColumns(open: true);
                if (!_chatDetailInfoPaneShown)
                {
                    _chatDetailInfoPaneShown = true;
                    ChatDetailInfoPaneHost.Visibility = Visibility.Visible;
                    AnimateChatDetailInfoSlideIn();
                }

                return;
            }

            if (_chatDetailInfoPaneShown)
            {
                _chatDetailInfoPaneShown = false;
                AnimateChatDetailInfoSlideOut(() =>
                {
                    if (ViewModel?.IsChatDetailInfoOpen == true)
                    {
                        return;
                    }

                    ChatDetailInfoPaneHost.Visibility = Visibility.Collapsed;
                    if (ChatDetailInfoPanel != null)
                    {
                        ChatDetailInfoPanel.Visibility = Visibility.Collapsed;
                    }

                    if (ChatDetailMemberInfoPanel != null)
                    {
                        ChatDetailMemberInfoPanel.Visibility = Visibility.Collapsed;
                    }

                    ApplyChatDetailInfoColumns(open: false);
                    if (ChatDetailInfoSlideTransform != null)
                    {
                        ChatDetailInfoSlideTransform.X = 0;
                    }
                });
                return;
            }

            ChatDetailInfoPaneHost.Visibility = Visibility.Collapsed;
            ApplyChatDetailInfoColumns(open: false);
        }

        private void ApplyChatDetailInfoColumns(bool open)
        {
            string state = !open
                ? "InfoClosed"
                : (ShouldUseFullScreenChatInfo() ? "InfoFullScreen" : "InfoDocked");
            VisualStateManager.GoToState(this, state, false);

            if (!open)
            {
                ChatColumn.Width = new GridLength(1, GridUnitType.Star);
                InfoColumn.Width = new GridLength(0);
                return;
            }

            if (ShouldUseFullScreenChatInfo())
            {
                ChatColumn.Width = new GridLength(0);
                InfoColumn.Width = new GridLength(1, GridUnitType.Star);
            }
            else
            {
                ChatColumn.Width = new GridLength(1, GridUnitType.Star);
                InfoColumn.Width = new GridLength(ChatPaneLayoutConstants.ChatDetailInfoPaneWidth);
            }
        }

        private void StopChatDetailInfoSlide()
        {
            try
            {
                _chatDetailInfoSlideStoryboard?.Stop();
            }
            catch
            {
            }

            _chatDetailInfoSlideStoryboard = null;
        }

        private double ResolveChatDetailInfoSlideWidth()
        {
            double width = ChatDetailInfoPaneHost != null ? ChatDetailInfoPaneHost.ActualWidth : 0;
            if (width <= 1 && ChatDetailInfoPanel != null)
            {
                width = ChatDetailInfoPanel.ActualWidth;
            }

            if (width <= 1 && ChatDetailMemberInfoPanel != null)
            {
                width = ChatDetailMemberInfoPanel.ActualWidth;
            }

            if (width > 1)
            {
                return width;
            }

            if (ShouldUseFullScreenChatInfo())
            {
                return Math.Max(ActualWidth, ChatPaneLayoutConstants.ChatDetailInfoPaneWidth);
            }

            return ChatPaneLayoutConstants.ChatDetailInfoPaneWidth;
        }

        private void AnimateChatDetailInfoSlideIn()
        {
            if (ChatDetailInfoSlideTransform == null)
            {
                return;
            }

            StopChatDetailInfoSlide();
            double from = ResolveChatDetailInfoSlideWidth();
            ChatDetailInfoSlideTransform.X = from;

            // After column layout, snap start offset to the real pane width then ease to 0.
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            {
                if (!_chatDetailInfoPaneShown || ChatDetailInfoSlideTransform == null)
                {
                    return;
                }

                StopChatDetailInfoSlide();
                from = ResolveChatDetailInfoSlideWidth();
                ChatDetailInfoSlideTransform.X = from;

                var anim = new DoubleAnimation
                {
                    To = 0,
                    Duration = TimeSpan.FromMilliseconds(240),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(anim, ChatDetailInfoSlideTransform);
                Storyboard.SetTargetProperty(anim, "X");

                var sb = new Storyboard();
                sb.Children.Add(anim);
                _chatDetailInfoSlideStoryboard = sb;
                sb.Begin();
            });
        }

        private void AnimateChatDetailInfoSlideOut(Action completed)
        {
            if (ChatDetailInfoSlideTransform == null)
            {
                completed?.Invoke();
                return;
            }

            StopChatDetailInfoSlide();
            double to = ResolveChatDetailInfoSlideWidth();

            var anim = new DoubleAnimation
            {
                To = to,
                Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            Storyboard.SetTarget(anim, ChatDetailInfoSlideTransform);
            Storyboard.SetTargetProperty(anim, "X");

            var sb = new Storyboard();
            sb.Children.Add(anim);
            sb.Completed += (s, e) =>
            {
                if (ReferenceEquals(_chatDetailInfoSlideStoryboard, sb))
                {
                    _chatDetailInfoSlideStoryboard = null;
                }

                completed?.Invoke();
            };
            _chatDetailInfoSlideStoryboard = sb;
            sb.Begin();
        }

        /// <summary>
        /// Info needs <see cref="ChatPaneLayoutConstants.ChatDetailInfoPaneWidth"/> beside the
        /// conversation. When this surface is narrower than twice that, info covers the pane.
        /// Measured on ChatDetail, not the window (list + detail can be wide while detail is not).
        /// </summary>
        private bool ShouldUseFullScreenChatInfo()
        {
            double width = ActualWidth;
            if (width <= 0 && ChatDetailGrid != null)
            {
                width = ChatDetailGrid.ActualWidth;
            }

            return width > 0 &&
                   width < ChatPaneLayoutConstants.ChatDetailInfoFullScreenBelowWidth;
        }

        /// <summary>Closes fullscreen media or the info pane; returns true if consumed.</summary>
        public bool TryConsumeBack()
        {
            if (TryCloseVideoViewer() || TryCloseImageViewer())
            {
                return true;
            }

            if (_attachMenuOpen)
            {
                SetAttachMenuOpen(false);
                return true;
            }

            if (ViewModel?.IsChatDetailInfoOpen == true)
            {
                ViewModel.CloseChatDetailInfo();
                return true;
            }

            return false;
        }

        private void MessageListView_Loaded(object sender, RoutedEventArgs e)
        {
            // Loaded dispara a CADA vez que a view volta a arvore visual (toda ida e
            // volta ao chat). Sem remover antes de assinar, os handlers se acumulavam
            // e todos rodavam a cada evento de rolagem -- o app ia degradando ate travar.
            if (_scrollViewer != null)
            {
                _scrollViewer.ViewChanged -= ScrollViewer_ViewChanged;
            }

            _scrollViewer = FindScrollViewer(MessageListView);
            if (_scrollViewer != null)
            {
                _scrollViewer.ViewChanged -= ScrollViewer_ViewChanged;
                _scrollViewer.ViewChanged += ScrollViewer_ViewChanged;
            }

            ApplyMessageListCacheLengthIfMobile();
            UpdateScrollToBottomButton();
        }

        private ScrollViewer FindScrollViewer(DependencyObject element)
        {
            if (element is ScrollViewer sv) return sv;
            for (int i = 0; i < Windows.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(element); i++)
            {
                var child = Windows.UI.Xaml.Media.VisualTreeHelper.GetChild(element, i);
                var result = FindScrollViewer(child);
                if (result != null) return result;
            }
            return null;
        }

        /// <summary>
        /// Mobile: shrink ItemsStackPanel cache so fewer off-screen bubbles stay realized (default ~4).
        /// </summary>
        private void ApplyMessageListCacheLengthIfMobile()
        {
            if (!_isWindowsMobile || MessageListView == null || _messageListCacheLengthApplied)
            {
                return;
            }

            try
            {
                ItemsStackPanel panel = MessageListView.ItemsPanelRoot as ItemsStackPanel;
                if (panel == null)
                {
                    panel = FindItemsStackPanel(MessageListView);
                }

                if (panel != null)
                {
                    panel.CacheLength = 0.5;
                    _messageListCacheLengthApplied = true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatDetailView] CacheLength: " + ex.Message);
            }
        }

        private static ItemsStackPanel FindItemsStackPanel(DependencyObject root)
        {
            if (root is ItemsStackPanel stack)
            {
                return stack;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                ItemsStackPanel found = FindItemsStackPanel(VisualTreeHelper.GetChild(root, i));
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private async void ScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            UpdateScrollToBottomButton();

            if (_scrollViewer == null || ViewModel == null || _activeChat == null) return;
            if (ViewModel.IsTimelineBusy) return;

            // Wait for the gesture to settle. Firing while e.IsIntermediate is true means the
            // load-more (and its ChangeView) lands on top of an active manipulation/fling, which
            // the platform discards - the viewport then ends the scroll at the top anyway.
            if (e.IsIntermediate) return;
            if (DateTime.UtcNow < _suppressLoadMoreUntilUtc) return;

            RequestVisibleParticipantAvatarsFromViewportThrottled();

            // Exige conteudo realmente rolavel. Sem isso, uma conversa curta abre com
            // offset baixo, dispara "carregar mais" imediatamente, prepende mensagens
            // antigas e a tela nunca assenta no fim.
            bool temConteudoRolavel = _scrollViewer.ExtentHeight > (_scrollViewer.ViewportHeight * 1.5);

            if (temConteudoRolavel &&
                _scrollViewer.VerticalOffset < 300 &&
                ViewModel.CanLoadMore)
            {
                Debug.WriteLine($"[ChatDetailView] TRIGGER HIT: Offset={_scrollViewer.VerticalOffset} < 300. Loading more...");
                await LoadMoreMessagesAsync();
            }
        }

        private void RequestVisibleParticipantAvatarsFromViewportThrottled()
        {
            if (DateTime.UtcNow - _lastVisibleAvatarRequestUtc < TimeSpan.FromMilliseconds(400))
            {
                return;
            }

            RequestVisibleParticipantAvatarsFromViewport();
        }

        /// <summary>
        /// Collects ParticipantJids from realized bubbles in the viewport and asks the VM
        /// to hydrate only those member pictures.
        /// </summary>
        private void RequestVisibleParticipantAvatarsFromViewport()
        {
            if (ViewModel == null || _activeChat == null || !_activeChat.IsGroup ||
                MessageListView == null || _messages == null)
            {
                return;
            }

            _lastVisibleAvatarRequestUtc = DateTime.UtcNow;
            var jids = new List<string>();
            int count = MessageListView.Items?.Count ?? 0;
            for (int i = 0; i < count; i++)
            {
                var container = MessageListView.ContainerFromIndex(i) as FrameworkElement;
                if (container == null)
                {
                    continue;
                }

                if (_scrollViewer != null)
                {
                    try
                    {
                        GeneralTransform transform = container.TransformToVisual(_scrollViewer);
                        double top = transform.TransformPoint(new Point(0, 0)).Y;
                        if (top + container.ActualHeight <= 0)
                        {
                            continue;
                        }

                        if (top >= _scrollViewer.ViewportHeight)
                        {
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("[ChatDetailView] Visible avatar transform: " + ex.Message);
                    }
                }

                var vm = MessageListView.Items[i] as ChatMessageViewModel;
                string participant = vm?.ParticipantJid;
                if (string.IsNullOrWhiteSpace(participant))
                {
                    participant = vm?.Model?.ParticipantJid;
                }

                if (!string.IsNullOrWhiteSpace(participant))
                {
                    jids.Add(participant);
                }
            }

            if (jids.Count > 0)
            {
                ViewModel.RequestVisibleParticipantAvatars(jids);
            }
        }

        private async Task LoadMoreMessagesAsync()
        {
            if (ViewModel == null || _activeChat == null || !ViewModel.CanLoadMore)
            {
                return;
            }

            // Arm before the await: near-top offset stays low during the fetch, and without this
            // a settled ViewChanged can start a second load-more on top of the first.
            _suppressLoadMoreUntilUtc = DateTime.UtcNow.AddMilliseconds(1500);

            try
            {
                string requestedJid = WhatsApp.GetCanonicalJid(_activeChat.JID);
                Debug.WriteLine($"[ChatDetailView] Loading more messages for {requestedJid}. Current: {_messages.Count}");

                // KeepItemsInView alone does not hold position here (variable bubble heights + many
                // Inserts + offset already near 0). Capture the first visible bubble and put it
                // back after layout.
                object anchor = CaptureTopVisibleMessage(out double anchorTopInViewport);
                double oldExtent = _scrollViewer?.ExtentHeight ?? 0;
                double oldOffset = _scrollViewer?.VerticalOffset ?? 0;

                ChatTimelineLoadMoreResult result = await ViewModel.LoadMoreMessagesAsync();
                if (_activeChat == null ||
                    !string.Equals(WhatsApp.GetCanonicalJid(_activeChat.JID), requestedJid, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (result != null && result.PrependedCount > 0)
                {
                    Debug.WriteLine($"[ChatDetailView] Prepended {result.PrependedCount} bubble VMs.");
                    bool isGroup = _activeChat.IsGroup ||
                        requestedJid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase);
                    RecomputeMessageRuns(_messages, isGroup);

                    await RestoreScrollAfterPrependAsync(anchor, anchorTopInViewport, oldExtent, oldOffset);
                    _suppressLoadMoreUntilUtc = DateTime.UtcNow.AddMilliseconds(800);
                }
                else if (result != null && result.WaitingForOnDemand)
                {
                    Debug.WriteLine($"[ChatDetailView] Waiting for on-demand history for {_activeChat.JID}");
                }
                else if (result != null && result.ReachedStart)
                {
                    Debug.WriteLine($"[ChatDetailView] Reached start of history for {_activeChat.JID}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ChatDetailView] Error loading more messages: {ex.Message}");
            }
        }

        /// <summary>
        /// First realized timeline row that intersects the viewport, plus its Y relative to the
        /// ScrollViewer top. Null when virtualization has not generated any container yet.
        /// </summary>
        private object CaptureTopVisibleMessage(out double topInViewport)
        {
            topInViewport = 0;
            if (MessageListView == null || _scrollViewer == null || _messages == null)
            {
                return null;
            }

            int count = MessageListView.Items?.Count ?? 0;
            for (int i = 0; i < count; i++)
            {
                var container = MessageListView.ContainerFromIndex(i) as FrameworkElement;
                if (container == null || container.ActualHeight <= 0)
                {
                    continue;
                }

                try
                {
                    GeneralTransform transform = container.TransformToVisual(_scrollViewer);
                    double top = transform.TransformPoint(new Point(0, 0)).Y;
                    if (top + container.ActualHeight <= 0)
                    {
                        continue;
                    }

                    if (top >= _scrollViewer.ViewportHeight)
                    {
                        break;
                    }

                    topInViewport = top;
                    return MessageListView.Items[i];
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[ChatDetailView] CaptureTopVisibleMessage: " + ex.Message);
                }
            }

            return null;
        }

        /// <summary>
        /// After older rows are prepended, bring the pre-load anchor back to the same viewport
        /// position. ScrollIntoView gets the item on screen; a second pass with TransformToVisual
        /// (or extent delta) fine-tunes once containers exist.
        /// </summary>
        private async Task RestoreScrollAfterPrependAsync(
            object anchor,
            double anchorTopInViewport,
            double oldExtent,
            double oldOffset)
        {
            EnsureMessageListScrollViewer();
            if (_scrollViewer == null)
            {
                return;
            }

            // Let ItemsStackPanel realize the new leading rows before we ask for containers.
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => { });
            await Task.Delay(16);

            if (anchor != null)
            {
                TryScrollIntoView(anchor, ScrollIntoViewAlignment.Leading);

                await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                {
                    if (_scrollViewer == null)
                    {
                        return;
                    }

                    var container = MessageListView.ContainerFromItem(anchor) as FrameworkElement;
                    if (container != null)
                    {
                        try
                        {
                            double top = container.TransformToVisual(_scrollViewer)
                                .TransformPoint(new Point(0, 0)).Y;
                            double delta = top - anchorTopInViewport;
                            if (Math.Abs(delta) > 1)
                            {
                                double target = Math.Max(0, _scrollViewer.VerticalOffset + delta);
                                _scrollViewer.ChangeView(null, target, null, true);
                                Debug.WriteLine(
                                    $"[ChatDetailView] Restored scroll via anchor delta={delta:0.#} -> {target:0.#}");
                                return;
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine("[ChatDetailView] Anchor fine-tune failed: " + ex.Message);
                        }
                    }

                    ApplyExtentDeltaScroll(oldExtent, oldOffset);
                });
            }
            else
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                    ApplyExtentDeltaScroll(oldExtent, oldOffset));
            }
        }

        private void ApplyExtentDeltaScroll(double oldExtent, double oldOffset)
        {
            if (_scrollViewer == null)
            {
                return;
            }

            double deltaExtent = _scrollViewer.ExtentHeight - oldExtent;
            if (deltaExtent <= 0.5)
            {
                return;
            }

            double target = Math.Max(0, oldOffset + deltaExtent);
            _scrollViewer.ChangeView(null, target, null, true);
            Debug.WriteLine(
                $"[ChatDetailView] Restored scroll via extent delta={deltaExtent:0.#} -> {target:0.#}");
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            // Fullscreen chrome / info pane — close overlay before leaving the chat.
            if (TryConsumeBack())
            {
                return;
            }

            if (ViewModel?.BackCommand?.CanExecute(null) == true)
            {
                ViewModel.BackCommand.Execute(null);
                return;
            }

            BackRequested?.Invoke(this, EventArgs.Empty);
        }

        private void HeaderInfoButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel?.OpenChatDetailInfoCommand?.CanExecute(null) == true)
            {
                ViewModel.OpenChatDetailInfoCommand.Execute(null);
            }
        }

        private void HeaderAvatar_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
            if (ViewModel?.OpenChatDetailInfoFromAvatarCommand?.CanExecute(null) == true)
            {
                ViewModel.OpenChatDetailInfoFromAvatarCommand.Execute(null);
            }
        }

        /// <summary>
        /// MenuFlyout Visibility bindings are unreliable on UWP — swap mute/unmute on open.
        /// </summary>
        private void ChatMoreFlyout_Opening(object sender, object e)
        {
            var flyout = sender as MenuFlyout;
            if (flyout == null || ViewModel == null)
            {
                return;
            }

            // Mute / pin can have changed since this chat was opened; re-read and swap
            // Visibility. MenuFlyout Visibility bindings are unreliable on UWP, so this is
            // done on Opening — texts stay on x:Uid, no Loc round-trip.
            ViewModel.RefreshLocalChatState();
            bool muted = ViewModel.ShowUnmuteOption;
            bool widgetPinned = ViewModel.IsWidgetPinned;
            foreach (var item in flyout.Items)
            {
                var menuItem = item as MenuFlyoutItem;
                var subItem = item as MenuFlyoutSubItem;
                string tag = (menuItem?.Tag as string) ?? (subItem?.Tag as string);

                if (string.Equals(tag, "localMuteSub", StringComparison.Ordinal) && subItem != null)
                {
                    subItem.Visibility = muted ? Visibility.Collapsed : Visibility.Visible;
                    subItem.Foreground = new SolidColorBrush(Windows.UI.Colors.White);
                }
                else if (string.Equals(tag, "unmute", StringComparison.Ordinal) && menuItem != null)
                {
                    menuItem.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;
                }
                else if (string.Equals(tag, "widgetPin", StringComparison.Ordinal) && menuItem != null)
                {
                    menuItem.Visibility = widgetPinned ? Visibility.Collapsed : Visibility.Visible;
                }
                else if (string.Equals(tag, "widgetUnpin", StringComparison.Ordinal) && menuItem != null)
                {
                    menuItem.Visibility = widgetPinned ? Visibility.Visible : Visibility.Collapsed;
                }
                else if (string.Equals(tag, "addContact", StringComparison.Ordinal) && menuItem != null)
                {
                    menuItem.Visibility = ViewModel.CanAddToAddressBook
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }
            }
        }

        private void UpdateEmptyBackButtonVisibility()
        {
            if (EmptyBackButton == null)
            {
                return;
            }

            bool narrow = false;
            try
            {
                var shell = App.Services?.GetService<ShellViewModel>();
                narrow = shell != null && shell.IsNarrowWindow;
            }
            catch
            {
            }

            EmptyBackButton.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Invoked from <see cref="Templates.MessageTemplates"/> when a loaded image is tapped.</summary>
        internal void OnImageOpenButtonClick(object sender, RoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            var vm = element?.DataContext as ChatMessageViewModel;
            if (vm == null || !vm.HasImage)
            {
                return;
            }

            OpenImageViewer(vm);
        }

        internal async void OpenInfoImage(ChatMessageViewModel vm)
        {
            if (vm == null)
            {
                return;
            }

            if (vm.NeedsImageDownload)
            {
                await vm.DownloadImageAsync();
            }

            if (vm.HasImage)
            {
                OpenImageViewer(vm);
            }
        }

        internal async void OpenInfoVideo(ChatMessageViewModel vm)
        {
            if (vm == null)
            {
                return;
            }

            if (vm.NeedsVideoDownload)
            {
                await vm.DownloadVideoAsync();
            }

            if (vm.HasLocalVideo)
            {
                OpenVideoViewer(vm);
            }
        }

        private void OpenImageViewer(ChatMessageViewModel messageVm)
        {
            if (ImageViewerOverlay == null || App.Services == null)
            {
                return;
            }

            TryCloseVideoViewer();

            var share = App.Services.GetService<IShareService>();
            var files = App.Services.GetService<IFilePicker>();
            if (share == null || files == null)
            {
                return;
            }

            var viewerVm = new ImageViewerViewModel(messageVm, share, files, _strings);
            ImageViewerOverlay.CloseRequested -= ImageViewerOverlay_CloseRequested;
            ImageViewerOverlay.ViewModel = viewerVm;
            ImageViewerOverlay.CloseRequested += ImageViewerOverlay_CloseRequested;
            ImageViewerOverlay.Visibility = Visibility.Visible;
        }

        private void ImageViewerOverlay_CloseRequested(object sender, EventArgs e)
        {
            TryCloseImageViewer();
        }

        private bool TryCloseImageViewer()
        {
            if (ImageViewerOverlay == null || ImageViewerOverlay.Visibility != Visibility.Visible)
            {
                return false;
            }

            ImageViewerOverlay.CloseRequested -= ImageViewerOverlay_CloseRequested;
            ImageViewerOverlay.ViewModel = null;
            ImageViewerOverlay.Visibility = Visibility.Collapsed;
            return true;
        }

        /// <summary>Opens Imgur-style fullscreen video; stops bubble audio first.</summary>
        internal void OnVideoOpenButtonClick(object sender, RoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            var vm = element?.DataContext as ChatMessageViewModel;
            if (vm == null || !vm.HasLocalVideo)
            {
                return;
            }

            OpenVideoViewer(vm);
        }

        internal void OnDocumentReadyContextRequested(object sender, RightTappedRoutedEventArgs e)
        {
            ShowDocumentReadyMenu(sender as FrameworkElement, e?.GetPosition(sender as UIElement) ?? default(Point));
            e.Handled = true;
        }

        internal void OnDocumentReadyHolding(object sender, HoldingRoutedEventArgs e)
        {
            if (e.HoldingState != Windows.UI.Input.HoldingState.Started)
            {
                return;
            }

            ShowDocumentReadyMenu(sender as FrameworkElement, e.GetPosition(sender as UIElement));
            e.Handled = true;
        }

        /// <summary>Ready-state context menu: Abrir / Salvar como.</summary>
        private void ShowDocumentReadyMenu(FrameworkElement anchor, Point position)
        {
            var vm = anchor?.DataContext as ChatMessageViewModel;
            if (anchor == null || vm == null || !vm.HasLocalDocument || ViewModel == null)
            {
                return;
            }

            string openLabel = LocalizedStrings.Get("ChatDetail_DocumentOpen.Label", "Open document");
            string saveLabel = LocalizedStrings.Get("ChatDetail_DocumentSaveAs.Label", "Save as…");

            var flyout = new MenuFlyout();
            var openItem = new MenuFlyoutItem
            {
                Text = openLabel,
                Icon = new FontIcon { Glyph = "\uE8E5", FontFamily = (FontFamily)Application.Current.Resources["IconFont"] }
            };
            openItem.Click += async (_, __) => await vm.OpenDocumentAsync();
            flyout.Items.Add(openItem);

            var saveItem = new MenuFlyoutItem
            {
                Text = saveLabel,
                Icon = new FontIcon { Glyph = "\uE792", FontFamily = (FontFamily)Application.Current.Resources["IconFont"] }
            };
            saveItem.Click += async (_, __) => await vm.SaveDocumentAsAsync();
            flyout.Items.Add(saveItem);

            try
            {
                flyout.ShowAt(anchor, position);
            }
            catch
            {
                try { flyout.ShowAt(anchor); } catch { }
            }
        }

        private void OpenVideoViewer(ChatMessageViewModel messageVm)
        {
            if (VideoViewerOverlay == null || messageVm == null)
            {
                return;
            }

            TryCloseImageViewer();
            StopAudioPlayback();

            var viewerVm = new VideoViewerViewModel(messageVm, _strings);
            VideoViewerOverlay.CloseRequested -= VideoViewerOverlay_CloseRequested;
            VideoViewerOverlay.ResolveSmtcMetadata = ResolveVideoSmtcMetadata;
            VideoViewerOverlay.ViewModel = viewerVm;
            VideoViewerOverlay.CloseRequested += VideoViewerOverlay_CloseRequested;
            VideoViewerOverlay.Visibility = Visibility.Visible;
        }

        private Tuple<string, string> ResolveVideoSmtcMetadata(ChatMessageViewModel messageVm)
        {
            string title;
            string artist;
            ResolveAudioSmtcMetadata(messageVm?.Model, out title, out artist);
            return Tuple.Create(title, artist);
        }

        private void VideoViewerOverlay_CloseRequested(object sender, EventArgs e)
        {
            TryCloseVideoViewer();
        }

        private bool TryCloseVideoViewer()
        {
            if (VideoViewerOverlay == null || VideoViewerOverlay.Visibility != Visibility.Visible)
            {
                return false;
            }

            VideoViewerOverlay.CloseRequested -= VideoViewerOverlay_CloseRequested;
            VideoViewerOverlay.ResolveSmtcMetadata = null;
            VideoViewerOverlay.ViewModel = null;
            VideoViewerOverlay.Visibility = Visibility.Collapsed;
            return true;
        }

        /// <summary>Chat currently shown in the detail surface (may lag list selection during refresh).</summary>
        public ChatItem ActiveChatItem => _activeChat;

        /// <summary>
        /// Shows header/composer immediately, then loads bubbles. Prefer
        /// <see cref="PrepareActiveChatAsync"/> + <see cref="CompleteActiveChatLoadAsync"/> when
        /// the host must switch VisualState (NarrowDetail) before SQLite work.
        /// </summary>
        public async Task SetActiveChatAsync(ChatItem chat)
        {
            await PrepareActiveChatAsync(chat);
            if (chat != null && HasActiveChat)
            {
                await CompleteActiveChatLoadAsync();
            }
        }

        /// <summary>
        /// Binds title/avatar and makes the detail surface visible without waiting for messages.
        /// The host should then switch pane state and call <see cref="CompleteActiveChatLoadAsync"/>.
        /// </summary>
        public Task PrepareActiveChatAsync(ChatItem chat)
        {
            TryCloseVideoViewer();
            TryCloseImageViewer();

            var service = WhatsApp;
            if (chat != null)
            {
                string canonicalJid = service.GetCanonicalJid(chat.JID);
                if (!string.IsNullOrWhiteSpace(canonicalJid) &&
                    !string.Equals(canonicalJid, chat.JID, StringComparison.OrdinalIgnoreCase))
                {
                    var canonicalChat = service.Chats.FirstOrDefault(c =>
                        string.Equals(service.GetCanonicalJid(c.JID), canonicalJid, StringComparison.OrdinalIgnoreCase));
                    if (canonicalChat != null)
                    {
                        chat = canonicalChat;
                    }
                    else
                    {
                        chat.JID = canonicalJid;
                    }
                }

                // Same conversation already open / loading — do not cancel in-flight load.
                // List rebuilds often replace ChatItem instances; only rebind the reference.
                if (_activeChat != null &&
                    string.Equals(
                        service.GetCanonicalJid(_activeChat.JID),
                        service.GetCanonicalJid(chat.JID),
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (!ReferenceEquals(_activeChat, chat))
                    {
                        _activeChat.PropertyChanged -= ActiveChat_PropertyChanged;
                        _activeChat = chat;
                        _activeChat.PropertyChanged += ActiveChat_PropertyChanged;
                        service.SetActiveChatJid(chat.JID);
                        ViewModel?.SyncActiveChat(chat);
                        if (ActiveChatGrid.Visibility == Visibility.Visible)
                        {
                            ApplyChatTitle(chat, service);
                            ApplyHeaderAvatar(chat);
                            ApplyHeaderActions(
                                isGroup: chat.IsGroup || (chat.JID ?? string.Empty).EndsWith("@g.us", StringComparison.OrdinalIgnoreCase),
                                visible: true);
                        }
                    }

                    return Task.CompletedTask;
                }
            }

            _chatLoadCts?.Cancel();
            _chatLoadCts?.Dispose();
            _chatLoadCts = new CancellationTokenSource();

            if (_activeChat != null)
            {
                _activeChat.PropertyChanged -= ActiveChat_PropertyChanged;
            }

            _activeChat = chat;
            service.SetActiveChatJid(chat?.JID);
            ViewModel?.SyncActiveChat(chat);

            // A menu belongs to the conversation it was opened over, so a different one arriving
            // takes it down with everything else that was on screen.
            SetAttachMenuOpen(false);

            ViewModel?.ResetTimelinePaging();
            CancelPresenceAnimation();

            if (chat == null)
            {
                ClearMessageHighlight();
                ClearTimelineMessages();
                ActiveChatGrid.Visibility = Visibility.Collapsed;
                EmptyStateGrid.Visibility = Visibility.Visible;
                UpdateEmptyBackButtonVisibility();
                PinnedMessageButton.Visibility = Visibility.Collapsed;
                _displayedPinnedMessage = null;
                _activePinnedMessages.Clear();
                _displayedPinnedIndex = 0;
                if (HeaderAvatar != null)
                {
                    HeaderAvatar.AvatarUrl = null;
                    HeaderAvatar.IsGroup = false;
                }
                ApplyHeaderActions(isGroup: false, visible: false);
                return Task.CompletedTask;
            }

            _activeChat.PropertyChanged += ActiveChat_PropertyChanged;
            ActiveChatGrid.Visibility = Visibility.Visible;
            EmptyStateGrid.Visibility = Visibility.Collapsed;
            if (EmptyBackButton != null)
            {
                EmptyBackButton.Visibility = Visibility.Collapsed;
            }

            ChatStatusText.Text = "";
            ChatStatusText.Opacity = 0;
            ChatStatusText.Visibility = Visibility.Collapsed;
            TitleTranslateTransform.Y = 0;

            ApplyChatTitle(chat, service);
            ApplyHeaderAvatar(chat);

            if (_scrollViewer == null)
            {
                _scrollViewer = FindScrollViewer(MessageListView);
                if (_scrollViewer != null)
                {
                    _scrollViewer.ViewChanged -= ScrollViewer_ViewChanged;
                    _scrollViewer.ViewChanged += ScrollViewer_ViewChanged;
                }
            }

            ClearMessageHighlight();
            ClearTimelineMessages();
            // Same ListView/ScrollViewer across chats: VerticalOffset survives Clear+Replace.
            ResetMessageListScrollOffset();
            _stickToBottomUntilUtc = DateTime.UtcNow.AddSeconds(2.5);
            _suppressLoadMoreUntilUtc = DateTime.UtcNow.AddSeconds(2.5);

            // Mark-read is I/O; do not hold the first paint for it.
            if (ViewModel != null)
            {
                _ = ViewModel.MarkChatOpenedAsync(chat);
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Loads the UI message window for the chat <see cref="PrepareActiveChatAsync"/> just showed.
        /// Yields one frame (a short extra wait on Mobile) so the detail pane can paint first.
        /// </summary>
        public async Task CompleteActiveChatLoadAsync()
        {
            ChatItem chat = _activeChat;
            if (chat == null || ViewModel == null)
            {
                return;
            }

            CancellationToken token = _chatLoadCts?.Token ?? CancellationToken.None;
            if (token.IsCancellationRequested)
            {
                return;
            }

            await YieldForChatSurfacePaintAsync();
            if (token.IsCancellationRequested || _activeChat == null ||
                !ReferenceEquals(_activeChat, chat))
            {
                return;
            }

            var service = WhatsApp;
            string requestedJid = service.GetCanonicalJid(chat.JID);
            Debug.WriteLine($"[ChatDetailView] Loading messages for {requestedJid}");

            ViewModel.BeginLoadingMessages();
            List<ChatMessage> messages;
            try
            {
                messages = await MessagesFacade.LoadMessagesForChatAsync(requestedJid);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ChatDetailView] LoadMessagesForChatAsync failed: {ex.Message}");
                ViewModel.EndLoadingMessages();
                return;
            }

            if (token.IsCancellationRequested || _activeChat == null ||
                !string.Equals(service.GetCanonicalJid(_activeChat.JID), requestedJid, StringComparison.OrdinalIgnoreCase))
            {
                ViewModel.EndLoadingMessages();
                return;
            }

            try
            {
            var visibleMessages = ChatDetailViewModel.TakeLastWindow(
                messages,
                ViewModel.InitialUiMessageWindow);

            bool activeIsGroup = chat.IsGroup ||
                requestedJid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase);
            ViewModel.StampGroupRemoteJid(visibleMessages, requestedJid);

            RecomputeMessageRuns(visibleMessages, activeIsGroup);
            ViewModel.ReplaceTimelineWindow(visibleMessages);
            UpdatePinnedBanner();

            if (visibleMessages.Count == 0)
            {
                ChatMessage fallback = ViewModel.ApplyPreviewFallback(chat, requestedJid, activeIsGroup);
                if (fallback != null)
                {
                    RecomputeMessageRuns(new List<ChatMessage> { fallback }, activeIsGroup);
                    UpdatePinnedBanner();
                    Debug.WriteLine($"[ChatDetailView] Applied preview fallback for {requestedJid}");
                }
            }
            else
            {
                // Last Message = newest by TimestampUtc (then MessageId), not "last bubble in the
                // window". Arrival / DB order can be shuffled; bubble runs re-order for display.
                ChatMessage lastMsg = null;
                DateTime tipUtc = DateTime.MinValue;
                for (int i = 0; i < visibleMessages.Count; i++)
                {
                    ChatMessage candidate = visibleMessages[i];
                    if (candidate == null || candidate.Timestamp == DateTime.MinValue)
                    {
                        continue;
                    }

                    DateTime candidateUtc = WhatsAppMapper.ToUtc(candidate.Timestamp);
                    if (lastMsg == null ||
                        candidateUtc > tipUtc ||
                        (candidateUtc == tipUtc &&
                         string.CompareOrdinal(candidate.Id ?? string.Empty, lastMsg.Id ?? string.Empty) > 0))
                    {
                        lastMsg = candidate;
                        tipUtc = candidateUtc;
                    }
                }

                if (lastMsg == null)
                {
                    _ = service.ReconcileChatPreviewsFromSqliteAsync(
                        new[] { requestedJid },
                        "chat-open-empty");
                }
                else
                {
                    bool isGroup = chat.IsGroup ||
                        (chat.JID ?? string.Empty).EndsWith("@g.us", StringComparison.OrdinalIgnoreCase);
                    string rawPreview = ChatPreviewNormalizer.FormatListPreview(lastMsg, isGroup);
                    string authorPrefix = ChatPreviewNormalizer.FormatListAuthorPrefix(
                        lastMsg,
                        isGroup,
                        _strings?.Get("Chat_SelfFallbackName", "You") ?? "You");
                    ChatPreviewNormalizer.Normalize(
                        rawPreview,
                        ChatPreviewNormalizer.InferKindFromMessage(lastMsg),
                        out var previewKind,
                        out var preview);

                    DateTime currentUtc = chat.LastMessageTimestampUtc.HasValue
                        ? WhatsAppMapper.ToUtc(chat.LastMessageTimestampUtc.Value)
                        : DateTime.MinValue;

                    MessageSendState loadedSendState =
                        HistoryLiveMessageMapper.FromStatus(lastMsg.Status, lastMsg.IsFromMe);

                    // Only advance by TimestampUtc, or refresh when MessageId / body / fromMe differ
                    // at the same second. When MessageId differs, trust the visible tip even if the
                    // strip timestamp was poisoned (Unspecified→ToUniversalTime / +3h).
                    bool tipIdDiffers = !string.IsNullOrWhiteSpace(lastMsg.Id) &&
                                        !string.Equals(chat.LastMessageId, lastMsg.Id, StringComparison.Ordinal);
                    bool shouldApply =
                        tipIdDiffers ||
                        tipUtc > currentUtc ||
                        (tipUtc == currentUtc &&
                         currentUtc != DateTime.MinValue &&
                         (!string.Equals(chat.LastMessage, preview, StringComparison.Ordinal) ||
                          chat.LastMessageKind != previewKind ||
                          !string.Equals(chat.LastMessageAuthor, authorPrefix, StringComparison.Ordinal) ||
                          chat.LastMessageIsFromMe != lastMsg.IsFromMe ||
                          chat.LastMessageSendState != loadedSendState)) ||
                        (currentUtc == DateTime.MinValue && tipUtc != DateTime.MinValue);

                    if (shouldApply)
                    {
                        chat.LastMessage = preview;
                        chat.LastMessageAuthor = authorPrefix;
                        chat.LastMessageKind = previewKind;
                        chat.LastMessageMentionedJids = lastMsg.MentionedJids != null && lastMsg.MentionedJids.Count > 0
                            ? new System.Collections.Generic.List<string>(lastMsg.MentionedJids)
                            : null;
                        chat.LastMessageIsFromMe = lastMsg.IsFromMe;
                        chat.LastMessageSendState = loadedSendState;
                        chat.LastMessageId = lastMsg.Id;
                        chat.LastMessageTimestampUtc = tipUtc;
                        chat.Timestamp = WhatsAppMapper.FormatTimestamp(
                            lastMsg.Timestamp,
                            LocalizedStrings.Get("Common_Yesterday", "Yesterday"));
                        service.PersistChatListRowsPublic(new[] { chat });
                        Debug.WriteLine(
                            "[ChatDetailView] List preview from newest tip id=" +
                            (lastMsg.Id ?? "?") +
                            " fromMe=" + lastMsg.IsFromMe +
                            " ts=" + tipUtc.ToString("O"));
                    }

                    _ = service.ReconcileChatPreviewsFromSqliteAsync(
                        new[] { requestedJid },
                        "chat-open");
                }
            }

            if (chat.IsPersonal)
            {
                ViewModel.StopPresenceWatch();
            }
            else if (!_isWindowsMobile)
            {
                ViewModel.StartPresenceWatch(chat.JID);
            }
            }
            finally
            {
                ViewModel.EndLoadingMessages();
            }

            // ChangeView needs scroll enabled; lock is cleared in finally above.
            if (!token.IsCancellationRequested &&
                _activeChat != null &&
                string.Equals(service.GetCanonicalJid(_activeChat.JID), requestedJid, StringComparison.OrdinalIgnoreCase))
            {
                ScrollToBottom();
                _ = StickScrollToBottomOnOpenAsync(token);
                ApplyMessageListCacheLengthIfMobile();
                RequestVisibleParticipantAvatarsFromViewport();
            }
        }

        /// <summary>
        /// Lets the detail VisualState and header layout paint before SQLite / VM materialization.
        /// Mobile needs a real delay: one dispatcher turn is not enough for NarrowDetail to appear.
        /// </summary>
        private async Task YieldForChatSurfacePaintAsync()
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => { });
            if (_isWindowsMobile)
            {
                await Task.Delay(48);
            }
        }

        /// <summary>
        /// Drops a leftover VerticalOffset from the previous chat. Without this, opening another
        /// conversation reuses the same absolute offset and looks "stuck" mid-timeline.
        /// </summary>
        private void ResetMessageListScrollOffset()
        {
            EnsureMessageListScrollViewer();
            if (_scrollViewer == null)
            {
                return;
            }

            try
            {
                _scrollViewer.ChangeView(null, 0, null, true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatDetailView] ResetMessageListScrollOffset: " + ex.Message);
            }
        }

        /// <summary>
        /// Opening a chat must land on the newest bubble. One ScrollToBottom is not enough:
        /// virtualization realizes heights after the first ChangeView, and KeepItemsInView can
        /// leave the viewport where the previous chat was. Retry until near-bottom or cancelled.
        /// </summary>
        private async Task StickScrollToBottomOnOpenAsync(CancellationToken token)
        {
            try
            {
                int[] delaysMs = { 50, 120, 200, 350 };
                for (int i = 0; i < delaysMs.Length; i++)
                {
                    await Task.Delay(delaysMs[i]);
                    if (token.IsCancellationRequested || _activeChat == null || _messages == null ||
                        _messages.Count == 0)
                    {
                        return;
                    }

                    ScrollToBottom();
                    if (IsNearBottom())
                    {
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatDetailView] StickScrollToBottomOnOpenAsync: " + ex.Message);
            }
        }

        private void ClearTimelineMessages()
        {
            if (ViewModel != null)
            {
                ViewModel.ClearTimeline();
                return;
            }

            if (_messages == null)
            {
                return;
            }

            for (int i = 0; i < _messages.Count; i++)
            {
                _messages[i]?.Detach();
            }

            _messages.Clear();
        }

        private void ActiveChat_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_activeChat == null)
            {
                return;
            }

            if (e.PropertyName == nameof(ChatItem.Name))
            {
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    ApplyChatTitle(_activeChat, WhatsApp);
                });
            }
            else if (e.PropertyName == nameof(ChatItem.GroupMembers) ||
                     e.PropertyName == nameof(ChatItem.HasGroupMembers))
            {
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    if (_messages == null || _messages.Count == 0 || _activeChat == null)
                    {
                        return;
                    }

                    bool isGroup = _activeChat.IsGroup ||
                        (!string.IsNullOrWhiteSpace(_activeChat.JID) &&
                         _activeChat.JID.IndexOf("@g.us", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (isGroup)
                    {
                        ViewModel.RebuildParticipantLookup();
                        RecomputeMessageRuns(_messages, isGroup: true);
                        if (!_isWindowsMobile && ViewModel != null && !_activeChat.IsPersonal)
                        {
                            ViewModel.StartPresenceWatch(_activeChat.JID);
                        }
                    }
                });
            }
            else if (e.PropertyName == nameof(ChatItem.AvatarUrl) ||
                     e.PropertyName == nameof(ChatItem.AvatarHighUrl) ||
                     e.PropertyName == nameof(ChatItem.Kind) ||
                     e.PropertyName == nameof(ChatItem.IsGroup))
            {
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    ApplyHeaderAvatar(_activeChat);
                });
            }
        }

        private void ApplyHeaderAvatar(ChatItem chat)
        {
            if (HeaderAvatar == null || chat == null)
            {
                ApplyHeaderActions(isGroup: false, visible: false);
                return;
            }

            bool isGroup = chat.IsGroup
                || (!string.IsNullOrWhiteSpace(chat.JID)
                    && chat.JID.IndexOf("@g.us", StringComparison.OrdinalIgnoreCase) >= 0);

            HeaderAvatar.IsGroup = isGroup;
            HeaderAvatar.AvatarUrl = chat.GetAvatarUrl(preferHigh: true);
            ApplyHeaderActions(isGroup, visible: true);
        }

        private void ApplyHeaderActions(bool isGroup, bool visible)
        {
            if (ContactHeaderActions != null)
            {
                ContactHeaderActions.Visibility = visible && !isGroup
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (GroupHeaderActions != null)
            {
                GroupHeaderActions.Visibility = visible && isGroup
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Groups/direct use resolved labels; Personal uses <see cref="ChatItem.GetNameResolved"/>
        /// (marker via <see cref="IStringResources"/>) with optional Runs + subtitle.
        /// </summary>
        private void ApplyChatTitle(ChatItem chat, IWhatsAppService service)
        {
            if (chat == null || ChatTitleText == null)
            {
                return;
            }

            bool isGroup = chat.IsGroup
                || (!string.IsNullOrWhiteSpace(chat.JID)
                    && chat.JID.IndexOf("@g.us", StringComparison.OrdinalIgnoreCase) >= 0);

            if (chat.IsPersonal)
            {
                string baseName = SelfChatNaming.StripMarker(chat.Name);
                if (string.IsNullOrWhiteSpace(baseName) && service != null)
                {
                    baseName = SelfChatNaming.StripMarker(service.ResolveDisplayName(chat.JID, "header"));
                }

                SetTitleWithSelfMarker(baseName);
                ShowPersonalSubtitle();
                return;
            }

            if (isGroup && !string.IsNullOrWhiteSpace(chat.Name))
            {
                SetTitlePlain(chat.GetNameResolved(_strings));
                return;
            }

            string display = service != null
                ? service.ResolveDisplayName(chat.JID, "header")
                : chat.GetNameResolved(_strings);
            SetTitlePlain(display);
        }

        private void SetTitlePlain(string text)
        {
            ChatTitleText.Inlines.Clear();
            ChatTitleText.Text = text ?? string.Empty;
        }

        private void SetTitleWithSelfMarker(string baseName)
        {
            ChatTitleText.Text = string.Empty;
            ChatTitleText.Inlines.Clear();

            string fallback = _strings != null
                ? _strings.Get("Chat_SelfFallbackName", "You")
                : "You";
            string marker = _strings != null
                ? _strings.Get("Chat_SelfMarker", "(You)")
                : "(You)";

            string name = string.IsNullOrWhiteSpace(baseName) ? fallback : baseName.Trim();

            var nameRun = new Windows.UI.Xaml.Documents.Run { Text = name };
            var markerRun = new Windows.UI.Xaml.Documents.Run
            {
                Text = " " + marker,
                FontWeight = Windows.UI.Text.FontWeights.Normal
            };

            if (ChatStatusText?.Foreground != null)
            {
                markerRun.Foreground = ChatStatusText.Foreground;
            }

            ChatTitleText.Inlines.Add(nameRun);
            ChatTitleText.Inlines.Add(markerRun);
        }

        private void ShowPersonalSubtitle()
        {
            if (ChatStatusText == null)
            {
                return;
            }

            ChatStatusText.Text = _strings != null
                ? _strings.Get("ChatDetail_PersonalSubtitle.Text", "Messages to myself")
                : "Messages to myself";
            ChatStatusText.Opacity = 1;
            ChatStatusText.Visibility = Visibility.Visible;
            if (TitleTranslateTransform != null)
            {
                TitleTranslateTransform.Y = 0;
            }
        }

        private void ScrollToBottomButton_Click(object sender, RoutedEventArgs e)
        {
            ScrollToBottom();
        }

        private void ScrollToBottom()
        {
            if (_messages.Count == 0)
            {
                return;
            }

            _suppressLoadMoreUntilUtc = DateTime.UtcNow.AddMilliseconds(900);
            EnsureMessageListScrollViewer();

            var last = _messages[_messages.Count - 1];
            TryScrollIntoView(last);
            ApplyScrollToMaxOffset();

            // One deferred correction after layout so the new bubble's realized height is included.
            // Fire-and-forget on the dispatcher: any exception here (e.g. ScrollIntoView's
            // well-known E_FAIL when the container isn't generated yet — more frequent on
            // slower/older ListView virtualization such as Windows 10 Mobile) has no awaiter,
            // so it must be swallowed here or it becomes a fatal unhandled exception.
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, async () =>
            {
                try
                {
                    await Task.Delay(120);
                    if (_messages.Count == 0)
                    {
                        return;
                    }

                    var currentLast = _messages[_messages.Count - 1];
                    TryScrollIntoView(currentLast);
                    ApplyScrollToMaxOffset();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[ChatDetailView] Deferred ScrollToBottom correction failed: " + ex.Message);
                }
            });
        }

        /// <summary>
        /// <see cref="ListViewBase.ScrollIntoView(object, ScrollIntoViewAlignment)"/> can throw
        /// COMException (E_FAIL) when called right after items are inserted and the container
        /// hasn't been generated yet — observed reliably on Windows 10 Mobile. Never let it
        /// escape as an unhandled exception (it would tear down the whole app via
        /// App.UnhandledException).
        /// </summary>
        private void TryScrollIntoView(object item, ScrollIntoViewAlignment alignment = ScrollIntoViewAlignment.Leading)
        {
            if (item == null)
            {
                return;
            }

            try
            {
                MessageListView.ScrollIntoView(item, alignment);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatDetailView] ScrollIntoView failed: " + ex.Message);
            }
        }

        private void EnsureMessageListScrollViewer()
        {
            if (_scrollViewer != null)
            {
                return;
            }

            _scrollViewer = FindScrollViewer(MessageListView);
            if (_scrollViewer != null)
            {
                _scrollViewer.ViewChanged -= ScrollViewer_ViewChanged;
                _scrollViewer.ViewChanged += ScrollViewer_ViewChanged;
            }
        }

        private void ApplyScrollToMaxOffset()
        {
            if (_scrollViewer == null)
            {
                EnsureMessageListScrollViewer();
            }

            if (_scrollViewer == null)
            {
                return;
            }

            double target = Math.Max(0, _scrollViewer.ExtentHeight - _scrollViewer.ViewportHeight);
            if (Math.Abs(_scrollViewer.VerticalOffset - target) > 0.5)
            {
                _scrollViewer.ChangeView(null, target, null, true);
            }

            UpdateScrollToBottomButton();
        }

        /// <summary>
        /// Keeps the viewport glued to the max offset while the optimistic bubble
        /// is inserted and laid out after an outgoing send.
        /// </summary>
        private async Task StickScrollToBottomAfterSendAsync()
        {
            try
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, ScrollToBottom);
                await Task.Delay(80);
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    if (_messages.Count > 0)
                    {
                        TryScrollIntoView(_messages[_messages.Count - 1]);
                    }
                    ApplyScrollToMaxOffset();
                });
                await Task.Delay(200);
                if (DateTime.UtcNow <= _stickToBottomUntilUtc)
                {
                    await Dispatcher.RunAsync(CoreDispatcherPriority.Low, ApplyScrollToMaxOffset);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatDetailView] StickScrollToBottomAfterSendAsync: " + ex.Message);
            }
        }

        private ChatMessageViewModel ToVm(ChatMessage message)
        {
            if (message == null) return null;
            return ViewModel != null
                ? ViewModel.CreateMessageVm(message)
                : _messageFactory.Create(message);
        }

        private static ChatMessage UnwrapMessage(object dataContext)
        {
            var vm = dataContext as ChatMessageViewModel;
            if (vm != null) return vm.Model;
            return dataContext as ChatMessage;
        }

        private void RecomputeMessageRuns(IList<ChatMessageViewModel> messages, bool isGroup = false)
        {
            if (messages == null || messages.Count == 0)
            {
                return;
            }

            var models = new List<ChatMessage>(messages.Count);
            for (int i = 0; i < messages.Count; i++)
            {
                models.Add(messages[i]?.Model);
            }

            RecomputeMessageRuns(models, isGroup);
        }

        private void RecomputeMessageRuns(IList<ChatMessage> messages, bool isGroup = false)
        {
            if (ViewModel == null)
            {
                return;
            }

            ViewModel.ApplyMessageRunLayout(messages, isGroup, _activeChat ?? ViewModel.ActiveChat);
        }

        /// <summary>
        /// Relabels Hoje/Ontem after local midnight. Timer lives here (WinRT); Core stays clock-free.
        /// </summary>
        private void StartDateSeparatorTimer()
        {
            if (_dateSeparatorTimer == null)
            {
                _dateSeparatorTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMinutes(1)
                };
                _dateSeparatorTimer.Tick += DateSeparatorTimer_Tick;
            }

            _dateSeparatorTimerDay = DateTime.Today;
            _dateSeparatorTimer.Start();
        }

        private void StopDateSeparatorTimer()
        {
            _dateSeparatorTimer?.Stop();
        }

        private void DateSeparatorTimer_Tick(object sender, object e)
        {
            if (DateTime.Today == _dateSeparatorTimerDay)
            {
                return;
            }

            _dateSeparatorTimerDay = DateTime.Today;
            ViewModel?.RefreshDateSeparators();
        }

        private void MessageService_ChatMessagesChanged(object sender, string updatedJid)
        {
            if (_activeChat == null || string.IsNullOrWhiteSpace(updatedJid))
            {
                return;
            }

            var service = WhatsApp;
            string activeCanonical = service.GetCanonicalJid(_activeChat.JID);
            string updatedCanonical = service.GetCanonicalJid(updatedJid);
            if (!string.Equals(activeCanonical, updatedCanonical, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (_isSyncingFromService)
            {
                _syncRequestedAgain = true;
                return;
            }

            _ = SyncMessagesFromServiceAsync();
        }


        private bool IsNearBottom()
        {
            if (_scrollViewer == null)
            {
                return true;
            }

            return (_scrollViewer.ExtentHeight - (_scrollViewer.VerticalOffset + _scrollViewer.ViewportHeight)) < 120;
        }

        /// <summary>
        /// Jump-to-latest chip: send bar visible (not recording / attach / group-lock)
        /// and the viewport is not already at the max vertical offset.
        /// </summary>
        private void UpdateScrollToBottomButton()
        {
            if (ScrollToBottomButton == null)
            {
                return;
            }

            bool sendBarVisible =
                !_attachMenuOpen &&
                ViewModel?.IsRecording != true &&
                ViewModel?.IsGroupLockedForMessages != true &&
                ComposerHost != null &&
                ComposerHost.Visibility == Visibility.Visible;

            bool awayFromBottom = false;
            if (_scrollViewer != null)
            {
                double maxOffset = Math.Max(0, _scrollViewer.ExtentHeight - _scrollViewer.ViewportHeight);
                awayFromBottom = maxOffset > 8 &&
                    (_scrollViewer.VerticalOffset + 120) < maxOffset;
            }

            ScrollToBottomButton.Visibility = sendBarVisible && awayFromBottom
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private bool ShouldStickScrollToBottom() =>
            DateTime.UtcNow <= _stickToBottomUntilUtc || IsNearBottom();

        private async Task SyncMessagesFromServiceAsync()
        {
            if (_activeChat == null || _isSyncingFromService)
            {
                return;
            }

            _isSyncingFromService = true;
            var service = WhatsApp;
            string requestedJid = service.GetCanonicalJid(_activeChat.JID);
            try
            {
                bool stickToBottom = ShouldStickScrollToBottom();
                List<ChatMessage> serviceMessages = await MessagesFacade.LoadRecentMessagesForSyncAsync(requestedJid);
                if (_activeChat == null ||
                    !string.Equals(service.GetCanonicalJid(_activeChat.JID), requestedJid, StringComparison.OrdinalIgnoreCase) ||
                    serviceMessages == null || serviceMessages.Count == 0)
                {
                    return;
                }

                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    if (_activeChat == null ||
                        !string.Equals(service.GetCanonicalJid(_activeChat.JID), requestedJid, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    bool changed = ViewModel.MergeTimelineFromService(serviceMessages, requestedJid);
                    UpdatePinnedBanner();

                    if (changed)
                    {
                        bool isGroup = _activeChat.IsGroup ||
                            requestedJid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase);
                        RecomputeMessageRuns(_messages, isGroup);
                        if (stickToBottom || ShouldStickScrollToBottom())
                        {
                            ScrollToBottom();
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ChatDetailView] SyncMessagesFromServiceAsync failed: {ex.Message}");
            }
            finally
            {
                _isSyncingFromService = false;
                if (_syncRequestedAgain)
                {
                    _syncRequestedAgain = false;
                    _ = SyncMessagesFromServiceAsync();
                }
            }
        }

        private void MessageInput_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != Windows.System.VirtualKey.Enter)
            {
                return;
            }

            // Shift+Enter inserts a newline (AcceptsReturn is false so Enter alone can send).
            var shift = Window.Current.CoreWindow
                .GetKeyState(Windows.System.VirtualKey.Shift);
            if ((shift & Windows.UI.Core.CoreVirtualKeyStates.Down) ==
                Windows.UI.Core.CoreVirtualKeyStates.Down)
            {
                var box = sender as TextBox;
                if (box == null)
                {
                    return;
                }

                int start = box.SelectionStart;
                string text = box.Text ?? string.Empty;
                string next = text.Insert(Math.Min(start, text.Length), "\r");
                box.Text = next;
                box.SelectionStart = start + 1;
                e.Handled = true;
                return;
            }

            if (ViewModel?.SendMessageCommand == null ||
                !ViewModel.SendMessageCommand.CanExecute(null))
            {
                return;
            }

            e.Handled = true;
            ViewModel.SendMessageCommand.Execute(null);
        }

        /// <summary>
        /// Keeps Auto height: if something stamps a fixed Height, clear it so wrap can grow again.
        /// </summary>
        private void MessageInput_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var box = sender as TextBox;
            if (box == null || double.IsNaN(box.Height))
            {
                return;
            }

            box.Height = double.NaN;
        }

        private void UpdatePinnedBanner()
        {
            DateTime now = DateTime.UtcNow;
            string previousId = _displayedPinnedMessage?.Id;
            _activePinnedMessages = _messages
                .Where(m => m != null && m.IsPinned && (!m.PinExpiresAtUtc.HasValue || m.PinExpiresAtUtc.Value > now))
                .OrderByDescending(m => m.PinnedAtUtc ?? DateTime.MinValue)
                .Take(3)
                .ToList();

            if (_activePinnedMessages.Count == 0)
            {
                _displayedPinnedMessage = null;
                _displayedPinnedIndex = 0;
                PinnedMessageButton.Visibility = Visibility.Collapsed;
                PinnedMessageTitleText.Text = LocalizedStrings.Get("ChatDetail_Pinned.Text");
                PinnedMessagePreviewText.Text = string.Empty;
                return;
            }

            int previousIndex = !string.IsNullOrWhiteSpace(previousId)
                ? _activePinnedMessages.FindIndex(m => string.Equals(m?.Id, previousId, StringComparison.Ordinal))
                : -1;
            _displayedPinnedIndex = previousIndex >= 0 ? previousIndex : 0;
            ShowPinnedBannerItem();
        }

        private void ShowPinnedBannerItem()
        {
            if (_activePinnedMessages == null || _activePinnedMessages.Count == 0)
            {
                PinnedMessageButton.Visibility = Visibility.Collapsed;
                return;
            }

            if (_displayedPinnedIndex < 0 || _displayedPinnedIndex >= _activePinnedMessages.Count)
            {
                _displayedPinnedIndex = 0;
            }

            _displayedPinnedMessage = _activePinnedMessages[_displayedPinnedIndex];
            string preview = _displayedPinnedMessage.Caption;
            if (string.IsNullOrWhiteSpace(preview)) preview = _displayedPinnedMessage.Content;
            PinnedMessagePreviewText.Text = string.IsNullOrWhiteSpace(preview)
                ? "[MÃƒÂ­dia]"
                : preview.Replace("\r", " ").Replace("\n", " ");
            PinnedMessageTitleText.Text = _activePinnedMessages.Count > 1
                ? LocalizedStrings.Format("ChatDetail_PinnedIndex", _displayedPinnedIndex + 1, _activePinnedMessages.Count)
                : LocalizedStrings.Get("ChatDetail_Pinned.Text");
            PinnedMessageButton.Visibility = Visibility.Visible;
        }

        private void PinnedMessageButton_Click(object sender, RoutedEventArgs e)
        {
            if (_displayedPinnedMessage == null) return;

            TryScrollIntoView(_displayedPinnedMessage);
            if (_activePinnedMessages.Count > 1)
            {
                _displayedPinnedIndex = (_displayedPinnedIndex + 1) % _activePinnedMessages.Count;
                ShowPinnedBannerItem();
            }
        }

        /// <summary>Invoked from <see cref="Templates.MessageTemplates"/> when group author name/avatar is tapped.</summary>
        internal void OnGroupParticipantTapped(object sender, TappedRoutedEventArgs e)
        {
            var sourceVm = (sender as FrameworkElement)?.DataContext as ChatMessageViewModel;
            string jid = sourceVm?.ParticipantJid;
            if (string.IsNullOrWhiteSpace(jid) || ViewModel == null)
            {
                return;
            }

            ViewModel.OpenGroupMemberInfoByJid(jid, sourceVm?.SenderName);
            e.Handled = true;
        }

        /// <summary>Invoked from <see cref="Templates.MessageTemplates"/> when the quote author name is tapped.</summary>
        internal void OnQuotedAuthorTapped(object sender, TappedRoutedEventArgs e)
        {
            if (ViewModel == null || ViewModel.ActiveChat?.IsGroup != true)
            {
                return;
            }

            string participantJid = ResolveQuotedParticipantJid(sender);
            string senderName = ResolveQuotedSenderName(sender);
            if (string.IsNullOrWhiteSpace(participantJid) && string.IsNullOrWhiteSpace(senderName))
            {
                return;
            }

            ViewModel.OpenQuotedAuthor(participantJid, senderName);
            e.Handled = true;
        }

        /// <summary>Invoked from <see cref="Templates.MessageTemplates"/> when the quote/reply block is tapped.</summary>
        internal void OnQuotedMessageTapped(object sender, TappedRoutedEventArgs e)
        {
            string quotedId = ResolveQuotedMessageId(sender);
            if (string.IsNullOrWhiteSpace(quotedId) || ViewModel == null)
            {
                return;
            }

            e.Handled = true;
            _ = NavigateToQuotedMessageAsync(quotedId);
        }

        private async Task NavigateToQuotedMessageAsync(string quotedMessageId)
        {
            if (ViewModel == null || string.IsNullOrWhiteSpace(quotedMessageId))
            {
                return;
            }

            ChatMessageViewModel target = ViewModel.FindMessageById(quotedMessageId);
            if (target == null)
            {
                target = await ViewModel.NavigateToQuotedMessageAsync(quotedMessageId).ConfigureAwait(true);
                if (target != null && _activeChat != null)
                {
                    bool isGroup = _activeChat.IsGroup ||
                        _activeChat.JID.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase);
                    RecomputeMessageRuns(_messages, isGroup);
                }
            }

            if (target == null)
            {
                return;
            }

            TryScrollIntoView(target);
            await FlashMessageHighlightAsync(target).ConfigureAwait(true);
        }

        /// <summary>
        /// Resolves the quoted stanza id from <see cref="FrameworkElement.Tag"/> or bubble VM
        /// (walks up so strip / name taps share the same id).
        /// </summary>
        private static string ResolveQuotedMessageId(object sender)
        {
            var current = sender as DependencyObject;
            while (current != null)
            {
                var fe = current as FrameworkElement;
                if (fe != null)
                {
                    if (fe.Tag is string tagId &&
                        !string.IsNullOrWhiteSpace(tagId) &&
                        tagId.IndexOf('@') < 0)
                    {
                        return tagId;
                    }

                    if (fe.DataContext is ChatMessageViewModel vm && !string.IsNullOrWhiteSpace(vm.QuotedMessageId))
                    {
                        return vm.QuotedMessageId;
                    }
                }

                current = Windows.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        /// <summary>
        /// Resolves quoted author JID from <see cref="FrameworkElement.Tag"/> or bubble VM.
        /// </summary>
        private static string ResolveQuotedParticipantJid(object sender)
        {
            var current = sender as DependencyObject;
            while (current != null)
            {
                var fe = current as FrameworkElement;
                if (fe != null)
                {
                    if (fe.Tag is string tagJid && tagJid.IndexOf('@') >= 0)
                    {
                        return tagJid;
                    }

                    if (fe.DataContext is ChatMessageViewModel vm &&
                        !string.IsNullOrWhiteSpace(vm.QuotedParticipantJid))
                    {
                        return vm.QuotedParticipantJid;
                    }
                }

                current = Windows.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        private static string ResolveQuotedSenderName(object sender)
        {
            var current = sender as DependencyObject;
            while (current != null)
            {
                if (current is FrameworkElement fe &&
                    fe.DataContext is ChatMessageViewModel vm &&
                    !string.IsNullOrWhiteSpace(vm.QuotedSenderName))
                {
                    return vm.QuotedSenderName;
                }

                current = Windows.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        private async Task FlashMessageHighlightAsync(ChatMessageViewModel target)
        {
            if (target == null) return;

            ClearMessageHighlight();

            int generation = ++_highlightGeneration;
            _highlightedMessage = target;
            target.IsHighlighted = true;

            try
            {
                await Task.Delay(1100);
            }
            catch
            {
                // Ignore â€” delay is only for UI timing.
            }

            if (generation != _highlightGeneration || _highlightedMessage != target)
            {
                return;
            }

            target.IsHighlighted = false;
            _highlightedMessage = null;
        }

        private void ClearMessageHighlight()
        {
            _highlightGeneration++;
            if (_highlightedMessage != null)
            {
                _highlightedMessage.IsHighlighted = false;
                _highlightedMessage = null;
            }
        }

        /// <summary>Invoked from <see cref="Templates.MessageTemplates"/> (external DataTemplate events).</summary>
        internal void OnMessageBubbleRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            ShowMessageActions(sender as FrameworkElement);
            e.Handled = true;
        }

        /// <summary>Invoked from <see cref="Templates.MessageTemplates"/> (external DataTemplate events).</summary>
        internal void OnMessageBubbleHolding(object sender, HoldingRoutedEventArgs e)
        {
            if (e.HoldingState == Windows.UI.Input.HoldingState.Started)
            {
                ShowMessageActions(sender as FrameworkElement);
                e.Handled = true;
            }
        }

        private void ShowMessageActions(FrameworkElement anchor)
        {
            var vm = anchor?.DataContext as ChatMessageViewModel;
            var message = vm?.Model ?? UnwrapMessage(anchor?.DataContext);
            if (message == null || _activeChat == null || string.IsNullOrWhiteSpace(message.Id)) return;

            if (vm == null && ViewModel != null)
            {
                vm = ViewModel.CreateMessageVm(message);
            }

            var flyout = new MenuFlyout();
            if (message.IsPinned)
            {
                AddPinAction(
                    flyout,
                    vm,
                    LocalizedStrings.Get("ChatDetail_UnpinMessage", "Unpin message"),
                    false,
                    0);
            }
            else
            {
                AddPinAction(
                    flyout,
                    vm,
                    LocalizedStrings.Get("ChatDetail_PinFor24Hours", "Pin for 24 hours"),
                    true,
                    86400);
                AddPinAction(
                    flyout,
                    vm,
                    LocalizedStrings.Get("ChatDetail_PinFor7Days", "Pin for 7 days"),
                    true,
                    604800);
                AddPinAction(
                    flyout,
                    vm,
                    LocalizedStrings.Get("ChatDetail_PinFor30Days", "Pin for 30 days"),
                    true,
                    2592000);
            }
            // The one-argument FlyoutBase.ShowAt overload requires Windows 10 1809.
            // Windows 10 Mobile 15063 supports the original MenuFlyout overload.
            flyout.ShowAt(anchor, new Windows.Foundation.Point(
                Math.Max(0, anchor.ActualWidth / 2),
                Math.Max(0, anchor.ActualHeight / 2)));
        }

        private void AddPinAction(MenuFlyout flyout, ChatMessageViewModel vm, string label, bool pin, uint durationSeconds)
        {
            var item = new MenuFlyoutItem { Text = label };
            item.Click += async (s, e) =>
            {
                if (vm?.Model == null)
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(vm.Model.RemoteJid) && _activeChat != null)
                {
                    vm.Model.RemoteJid = _activeChat.JID;
                }

                await vm.SetPinnedAsync(pin, durationSeconds);
                UpdatePinnedBanner();
            };
            flyout.Items.Add(item);
        }

        /// <summary>Play / resume / pause for a ready local audio bubble + SMTC.</summary>
        internal async void OnAudioPlayButtonClick(object sender, RoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            await PlayOrPauseAudioAsync(element?.DataContext as ChatMessageViewModel);
        }

        internal async void PlayOrPauseAudioFromInfo(ChatMessageViewModel vm)
        {
            if (vm == null)
            {
                return;
            }

            if (!vm.HasLocalAudio || vm.ShowAudioDownloadIcon || vm.NeedsAudioDownload)
            {
                await vm.DownloadAudioAsync();
                return;
            }

            await PlayOrPauseAudioAsync(vm);
        }

        private async System.Threading.Tasks.Task PlayOrPauseAudioAsync(ChatMessageViewModel vm)
        {
            var message = vm?.Model;
            if (message == null || !message.IsAudio || ViewModel == null)
            {
                return;
            }

            try
            {
                EnsureAudioMediaPlayer();
                var player = _audioMediaPlayer;
                if (player == null)
                {
                    LogAudio("play-no-player", message, null);
                    return;
                }

                // Toggle pause on the same bubble.
                if (_playingAudioVm != null &&
                    ReferenceEquals(_playingAudioVm, vm) &&
                    vm.AudioPlaybackStatus == AudioPlaybackStatus.Playing)
                {
                    LogAudio("pause", message, null);
                    player.Pause();
                    vm.AudioPlaybackStatus = AudioPlaybackStatus.Paused;
                    StopAudioPositionTimer();
                    _voiceRouting?.EndSession();
                    return;
                }

                // Resume paused bubble without resetting source.
                if (_playingAudioVm != null &&
                    ReferenceEquals(_playingAudioVm, vm) &&
                    vm.AudioPlaybackStatus == AudioPlaybackStatus.Paused)
                {
                    LogAudio("resume", message, null);
                    ApplySmtcMetadata(message);
                    player.Play();
                    _voiceRouting?.BeginSession();
                    vm.AudioPlaybackStatus = AudioPlaybackStatus.Playing;
                    StartAudioPositionTimer();
                    return;
                }

                // Always resolve a playable URI (network if needed; oggâ†’wav via Concentus on Mobile).
                if (string.IsNullOrWhiteSpace(message.AudioUri) && vm != null)
                {
                    vm.AudioPlaybackStatus = AudioPlaybackStatus.Downloading;
                }

                LogAudio("play-ensure", message, "uriIn=" + (message.AudioUri ?? "null"));
                string uri = vm != null
                    ? await vm.EnsureAudioReadyAsync(showErrorDialog: true)
                    : null;
                if (string.IsNullOrWhiteSpace(uri))
                {
                    LogAudio("play-ensure-empty", message, null);
                    vm?.MarkAudioUnavailable();
                    return;
                }

                // Stop any other bubble.
                if (_playingAudioVm != null && !ReferenceEquals(_playingAudioVm, vm))
                {
                    _playingAudioVm.ResetAudioPlaybackToReady();
                }

                LogAudio(
                    "play-start",
                    message,
                    string.Format(
                        "uri={0} mime={1} mobile={2}",
                        uri,
                        message.AudioMimeType ?? "?",
                        _isWindowsMobile));

                ApplySmtcMetadata(message);
                player.Source = MediaSource.CreateFromUri(new Uri(uri));
                _playingAudioMessage = message;
                _playingAudioVm = vm;
                if (vm != null)
                {
                    vm.AudioPlaybackPositionSeconds = 0;
                    vm.AudioPlaybackStatus = AudioPlaybackStatus.Playing;
                }

                player.Play();
                // Route after Play — AudioRoutingManager needs an active Communications stream.
                _voiceRouting?.BeginSession();
                StartAudioPositionTimer();
            }
            catch (Exception ex)
            {
                LogAudio("play-exception", message, ex.ToString());
                vm?.MarkAudioUnavailable();
            }
        }

        private void EnsureAudioMediaPlayer()
        {
            if (_audioMediaPlayer != null)
            {
                return;
            }

            bool useCommunications =
                _voiceRouting != null &&
                string.Equals(
                    _voiceRouting.PreferredAudioCategory,
                    "Communications",
                    StringComparison.OrdinalIgnoreCase);

            _audioMediaPlayer = new MediaPlayer
            {
                // Mobile: Communications enables AudioRoutingManager (speaker ↔ earpiece).
                // Desktop: Media uses the system default device.
                AudioCategory = useCommunications || _isWindowsMobile
                    ? MediaPlayerAudioCategory.Communications
                    : MediaPlayerAudioCategory.Media,
                AutoPlay = false
            };
            _audioMediaPlayer.MediaEnded += AudioMediaPlayer_MediaEnded;
            _audioMediaPlayer.MediaFailed += AudioMediaPlayer_MediaFailed;
            _audioMediaPlayer.MediaOpened += AudioMediaPlayer_MediaOpened;
            _audioMediaPlayer.PlaybackSession.PlaybackStateChanged += AudioPlaybackSession_PlaybackStateChanged;
            _audioMediaPlayer.CommandManager.IsEnabled = true;
            AudioPlayer.SetMediaPlayer(_audioMediaPlayer);
            _voiceRouting?.AttachPlayer(_audioMediaPlayer);
        }

        private async void AudioPlaybackSession_PlaybackStateChanged(MediaPlaybackSession sender, object args)
        {
            try
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    if (_playingAudioVm == null || _audioMediaPlayer == null)
                    {
                        return;
                    }

                    var state = _audioMediaPlayer.PlaybackSession.PlaybackState;
                    if (state == MediaPlaybackState.Paused &&
                        _playingAudioVm.AudioPlaybackStatus == AudioPlaybackStatus.Playing)
                    {
                        // Keep current second frozen (SMTC / system pause).
                        _playingAudioVm.AudioPlaybackStatus = AudioPlaybackStatus.Paused;
                        StopAudioPositionTimer();
                    }
                    else if (state == MediaPlaybackState.Playing &&
                             _playingAudioVm.AudioPlaybackStatus == AudioPlaybackStatus.Paused)
                    {
                        _playingAudioVm.AudioPlaybackStatus = AudioPlaybackStatus.Playing;
                        StartAudioPositionTimer();
                    }
                });
            }
            catch
            {
            }
        }

        private void StartAudioPositionTimer()
        {
            if (_audioPositionTimer == null)
            {
                _audioPositionTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(250)
                };
                _audioPositionTimer.Tick += AudioPositionTimer_Tick;
            }

            _audioPositionTimer.Start();
        }

        private void StopAudioPositionTimer()
        {
            try
            {
                _audioPositionTimer?.Stop();
            }
            catch
            {
            }
        }

        private void AudioPositionTimer_Tick(object sender, object e)
        {
            try
            {
                if (_playingAudioVm == null ||
                    _audioMediaPlayer == null ||
                    _playingAudioVm.AudioPlaybackStatus != AudioPlaybackStatus.Playing)
                {
                    return;
                }

                double secs = _audioMediaPlayer.PlaybackSession.Position.TotalSeconds;
                if (secs < 0)
                {
                    secs = 0;
                }

                _playingAudioVm.AudioPlaybackPositionSeconds = (uint)Math.Floor(secs);
            }
            catch
            {
            }
        }

        /// <summary>Seek the active bubble's MediaPlayer (Imgur PlayerSlider scrub).</summary>
        internal void SeekAudioPlayback(ChatMessageViewModel vm, double seconds)
        {
            if (vm == null ||
                _playingAudioVm == null ||
                !ReferenceEquals(_playingAudioVm, vm) ||
                _audioMediaPlayer == null)
            {
                return;
            }

            try
            {
                if (seconds < 0)
                {
                    seconds = 0;
                }

                double max = _audioMediaPlayer.PlaybackSession.NaturalDuration.TotalSeconds;
                if (!double.IsNaN(max) && max > 0 && seconds > max)
                {
                    seconds = max;
                }

                _audioMediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(seconds);
                vm.AudioPlaybackPositionSeconds = (uint)Math.Floor(seconds);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatDetailView] Audio seek failed: " + ex.Message);
            }
        }

        /// <summary>
        /// SMTC display:
        /// Group → Title = group name, Artist = message author.
        /// 1:1 → Title = "Unison", Artist = message author.
        /// Must be re-applied after Source is set (MediaOpened) — assigning Source clears DisplayUpdater.
        /// </summary>
        private void ApplySmtcMetadata(ChatMessage message)
        {
            try
            {
                var player = _audioMediaPlayer;
                if (player == null || message == null)
                {
                    return;
                }

                var smtc = player.SystemMediaTransportControls;
                smtc.IsEnabled = true;
                smtc.IsPlayEnabled = true;
                smtc.IsPauseEnabled = true;

                string title;
                string artist;
                ResolveAudioSmtcMetadata(message, out title, out artist);

                var updater = smtc.DisplayUpdater;
                updater.ClearAll();
                updater.Type = MediaPlaybackType.Music;
                updater.AppMediaId = "Unison.VoiceNote";
                updater.MusicProperties.Title = title;
                updater.MusicProperties.Artist = artist;
                updater.Update();

                LogAudio("smtc", message, "title=\"" + title + "\" artist=\"" + artist + "\"");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatDetailView] SMTC update failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Group: title = group name, artist = sender.
        /// 1:1: title = Unison, artist = sender.
        /// </summary>
        private void ResolveAudioSmtcMetadata(ChatMessage message, out string title, out string artist)
        {
            var chat = ViewModel?.ActiveChat ?? _activeChat;
            bool isGroup = chat != null &&
                (chat.IsGroup ||
                 (!string.IsNullOrWhiteSpace(chat.JID) &&
                  chat.JID.IndexOf("@g.us", StringComparison.OrdinalIgnoreCase) >= 0));

            artist = ResolveMessageAuthorForSmtc(message, chat);

            if (isGroup)
            {
                title = !string.IsNullOrWhiteSpace(chat?.Name)
                    ? chat.Name.Trim()
                    : null;
                if (string.IsNullOrWhiteSpace(title))
                {
                    title = chat?.GetNameResolved(_strings);
                }

                if (string.IsNullOrWhiteSpace(title))
                {
                    title = "Chat";
                }

                return;
            }

            title = "Unison";
        }

        private string ResolveMessageAuthorForSmtc(ChatMessage message, ChatItem chat)
        {
            if (message == null)
            {
                return "Chat";
            }

            if (message.IsFromMe)
            {
                return _strings != null
                    ? _strings.Get("Chat_SelfFallbackName", "You")
                    : "You";
            }

            if (!string.IsNullOrWhiteSpace(message.SenderName) &&
                !string.Equals(message.SenderName, "Me", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(message.SenderName, "You", StringComparison.OrdinalIgnoreCase))
            {
                return message.SenderName.Trim();
            }

            string jid = message.ParticipantJid;
            if (string.IsNullOrWhiteSpace(jid))
            {
                jid = chat?.JID;
            }

            try
            {
                if (WhatsApp != null && !string.IsNullOrWhiteSpace(jid))
                {
                    string resolved = WhatsApp.ResolveDisplayName(jid, "sender");
                    if (!string.IsNullOrWhiteSpace(resolved))
                    {
                        return resolved.Trim();
                    }
                }
            }
            catch
            {
            }

            if (chat != null)
            {
                string name = chat.GetNameResolved(_strings);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name.Trim();
                }

                if (!string.IsNullOrWhiteSpace(chat.Name))
                {
                    return chat.Name.Trim();
                }
            }

            return "Chat";
        }

        private void StopAudioPlayback()
        {
            StopAudioPositionTimer();
            _voiceRouting?.EndSession();
            try
            {
                if (_audioMediaPlayer != null)
                {
                    _audioMediaPlayer.Pause();
                    _audioMediaPlayer.Source = null;
                    try
                    {
                        _audioMediaPlayer.SystemMediaTransportControls.IsEnabled = false;
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }

            _playingAudioVm?.ResetAudioPlaybackToReady();
            _playingAudioVm = null;
            _playingAudioMessage = null;
        }

        private async void AudioMediaPlayer_MediaOpened(MediaPlayer sender, object args)
        {
            try
            {
                string detail = null;
                try
                {
                    var session = sender?.PlaybackSession;
                    detail = string.Format(
                        "durationSec={0:0.###} state={1}",
                        session != null ? session.NaturalDuration.TotalSeconds : -1,
                        session != null ? session.PlaybackState.ToString() : "?");
                }
                catch
                {
                    detail = "opened";
                }

                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    LogAudio("media-opened", _playingAudioMessage, detail);
                    // Source assignment clears DisplayUpdater — re-apply on open.
                    if (_playingAudioMessage != null)
                    {
                        ApplySmtcMetadata(_playingAudioMessage);
                    }

                    // Stream is live — reassert speaker if session already began.
                    if (_playingAudioVm != null &&
                        _playingAudioVm.AudioPlaybackStatus == AudioPlaybackStatus.Playing)
                    {
                        _voiceRouting?.BeginSession();
                    }
                });
            }
            catch
            {
            }
        }

        private async void AudioMediaPlayer_MediaEnded(MediaPlayer sender, object args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                StopAudioPositionTimer();
                _voiceRouting?.EndSession();
                _playingAudioVm?.ResetAudioPlaybackToReady();
                _playingAudioVm = null;
                _playingAudioMessage = null;
            });
        }

        private async void AudioMediaPlayer_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            string detail = string.Format(
                "error={0} ext={1} msg={2}",
                args?.Error,
                args?.ExtendedErrorCode,
                args?.ErrorMessage);
            LogAudio("media-failed", _playingAudioMessage, detail);
            Debug.WriteLine(string.Format("[ChatDetailView] MediaPlayer failed: {0}", args?.ErrorMessage));
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                StopAudioPositionTimer();
                _voiceRouting?.EndSession();
                if (_playingAudioVm != null)
                {
                    _playingAudioVm.MarkAudioUnavailable();
                }

                _playingAudioVm = null;
                _playingAudioMessage = null;
            });
        }

        /// <summary>Session log (+ DebugView) for mobile audio diagnosis â€” always captured.</summary>
        private void LogAudio(string stage, ChatMessage message, string details)
        {
            string id = message?.Id ?? "?";
            string line = string.Format(
                "[Audio/{0}] id={1} {2}",
                stage,
                id,
                details ?? string.Empty);
            try
            {
                Debug.WriteLine(line);
                SessionLogger.Instance.WriteAlways(line);
                App.Services?.GetService<IRuntimeDiagnostics>()?.Write("Audio", stage, line);
            }
            catch
            {
            }
        }

        /// <summary>Trash: fade timer â†’ slide red mic onto trash â†’ flash trash â†’ cancel recording.</summary>
        private async void CancelRecordingButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null || !ViewModel.IsRecording || _cancelRecordingAnimating)
            {
                return;
            }

            _cancelRecordingAnimating = true;
            CancelRecordingButton.IsEnabled = false;
            try
            {
                await PlayCancelRecordingAnimationAsync();
                if (ViewModel.CancelRecordingCommand != null &&
                    ViewModel.CancelRecordingCommand.CanExecute(null))
                {
                    ViewModel.CancelRecordingCommand.Execute(null);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatDetailView] Cancel recording anim: " + ex.Message);
                try
                {
                    if (ViewModel.CancelRecordingCommand?.CanExecute(null) == true)
                    {
                        ViewModel.CancelRecordingCommand.Execute(null);
                    }
                }
                catch
                {
                }
            }
            finally
            {
                ResetRecordingOverlayVisuals();
                _cancelRecordingAnimating = false;
                CancelRecordingButton.IsEnabled = true;
            }
        }

        private Task PlayCancelRecordingAnimationAsync()
        {
            var tcs = new TaskCompletionSource<bool>();

            try
            {
                RecordingOverlay?.UpdateLayout();
                double slideX = 0;
                if (RecordingMicIcon != null && RecordingTrashIcon != null && RecordingOverlay != null)
                {
                    GeneralTransform micToOverlay = RecordingMicIcon.TransformToVisual(RecordingOverlay);
                    GeneralTransform trashToOverlay = RecordingTrashIcon.TransformToVisual(RecordingOverlay);
                    Point micPt = micToOverlay.TransformPoint(new Point(0, 0));
                    Point trashPt = trashToOverlay.TransformPoint(new Point(0, 0));
                    slideX = trashPt.X - micPt.X;
                }

                var storyboard = new Storyboard();

                if (RecordingElapsedText != null)
                {
                    var fade = new DoubleAnimation
                    {
                        To = 0,
                        Duration = TimeSpan.FromMilliseconds(220),
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                    };
                    Storyboard.SetTarget(fade, RecordingElapsedText);
                    Storyboard.SetTargetProperty(fade, "Opacity");
                    storyboard.Children.Add(fade);
                }

                if (RecordingMicTranslate != null)
                {
                    var slide = new DoubleAnimation
                    {
                        To = slideX,
                        BeginTime = TimeSpan.FromMilliseconds(180),
                        Duration = TimeSpan.FromMilliseconds(320),
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
                    };
                    Storyboard.SetTarget(slide, RecordingMicTranslate);
                    Storyboard.SetTargetProperty(slide, "X");
                    storyboard.Children.Add(slide);
                }

                if (RecordingTrashIcon != null)
                {
                    RecordingTrashIcon.Foreground = new SolidColorBrush(Colors.Red);

                    var flashDown = new DoubleAnimation
                    {
                        From = 1,
                        To = 0.25,
                        BeginTime = TimeSpan.FromMilliseconds(500),
                        Duration = TimeSpan.FromMilliseconds(110),
                        AutoReverse = true,
                        RepeatBehavior = new RepeatBehavior(2)
                    };
                    Storyboard.SetTarget(flashDown, RecordingTrashIcon);
                    Storyboard.SetTargetProperty(flashDown, "Opacity");
                    storyboard.Children.Add(flashDown);
                }

                EventHandler<object> completed = null;
                completed = (s, args) =>
                {
                    storyboard.Completed -= completed;
                    tcs.TrySetResult(true);
                };
                storyboard.Completed += completed;
                storyboard.Begin();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatDetailView] Build cancel anim failed: " + ex.Message);
                tcs.TrySetResult(false);
            }

            return tcs.Task;
        }

        private void ResetRecordingOverlayVisuals()
        {
            try
            {
                if (RecordingElapsedText != null)
                {
                    RecordingElapsedText.Opacity = 1;
                }

                if (RecordingMicTranslate != null)
                {
                    RecordingMicTranslate.X = 0;
                    RecordingMicTranslate.Y = 0;
                }

                if (RecordingTrashIcon != null)
                {
                    RecordingTrashIcon.Opacity = 1;
                    Brush muted = null;
                    try
                    {
                        muted = Resources["ChatDetailMutedTextBrush"] as Brush
                            ?? Application.Current.Resources["ChatDetailMutedTextBrush"] as Brush;
                    }
                    catch
                    {
                    }

                    RecordingTrashIcon.Foreground = muted ?? new SolidColorBrush(Color.FromArgb(255, 136, 136, 136));
                }

                if (RecordingMicIcon != null)
                {
                    RecordingMicIcon.Opacity = 1;
                }
            }
            catch
            {
            }
        }

        #region Presence Animation

        private void CancelPresenceAnimation()
        {
            try
            {
                _presenceAnimationCts?.Cancel();
            }
            catch
            {
            }

            _presenceAnimationCts?.Dispose();
            _presenceAnimationCts = null;
            ViewModel?.StopPresenceWatch();
        }

        private void ViewModel_PresenceAnimationRequested(object sender, string statusText)
        {
            _ = RunPresenceAnimationAsync(statusText);
        }

        private async Task RunPresenceAnimationAsync(string statusText)
        {
            try
            {
                _presenceAnimationCts?.Cancel();
                _presenceAnimationCts?.Dispose();
                _presenceAnimationCts = new CancellationTokenSource();
                var ct = _presenceAnimationCts.Token;

                bool isGroup = _activeChat != null &&
                    (_activeChat.IsGroup ||
                     (!string.IsNullOrWhiteSpace(_activeChat.JID) &&
                      _activeChat.JID.IndexOf("@g.us", StringComparison.OrdinalIgnoreCase) >= 0));

                // Loop every ~90s while this chat stays open (cancel on leave / Unloaded).
                while (!ct.IsCancellationRequested)
                {
                    if (isGroup)
                    {
                        await AnimateGroupStatusSequenceAsync(statusText, ct);
                    }
                    else if (!string.IsNullOrEmpty(statusText))
                    {
                        await AnimateStatusSequenceAsync(statusText, ct);
                    }
                    else
                    {
                        await AnimateFallbackOnlyAsync(ct);
                    }

                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(90), ct);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatDetailView] Presence animation: " + ex.Message);
            }
        }

        /// <summary>
        /// Group: hint → alphabetical member names → fade out (then the outer loop waits ~90s).
        /// </summary>
        private async Task AnimateGroupStatusSequenceAsync(string statusText, CancellationToken ct)
        {
            try
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                string hint = !string.IsNullOrWhiteSpace(statusText)
                    ? statusText
                    : (_strings != null
                        ? _strings.Get("ChatDetail_SelectForGroupInfo", "tap here for group info")
                        : "tap here for group info");

                ChatStatusText.Text = hint;
                AnimateSlideUp();
                AnimateFadeIn(ChatStatusText);

                await Task.Delay(5000, ct);
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                AnimateFadeOut(ChatStatusText);
                await Task.Delay(250, ct);
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                string members = ViewModel?.FormatGroupMemberNamesSummary(_activeChat);
                if (!string.IsNullOrWhiteSpace(members))
                {
                    ChatStatusText.Text = members;
                    AnimateFadeIn(ChatStatusText);

                    await Task.Delay(6000, ct);
                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }

                    AnimateFadeOut(ChatStatusText);
                    await Task.Delay(250, ct);
                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }
                }

                AnimateSlideBack();
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>
        /// Full sequence: show presence status 5s → crossfade to "select for contact info" 5s → fade out → slide back
        /// </summary>
        private async Task AnimateStatusSequenceAsync(string statusText, CancellationToken ct)
        {
            try
            {
                if (ct.IsCancellationRequested) return;

                ChatStatusText.Text = statusText;
                AnimateSlideUp();
                AnimateFadeIn(ChatStatusText);

                await Task.Delay(5000, ct);
                if (ct.IsCancellationRequested) return;

                AnimateFadeOut(ChatStatusText);
                await Task.Delay(250, ct);
                if (ct.IsCancellationRequested) return;

                ChatStatusText.Text = GetSelectForContactInfoText();
                AnimateFadeIn(ChatStatusText);

                await Task.Delay(5000, ct);
                if (ct.IsCancellationRequested) return;

                AnimateFadeOut(ChatStatusText);
                await Task.Delay(250, ct);
                if (ct.IsCancellationRequested) return;
                AnimateSlideBack();
            }
            catch (OperationCanceledException) { }
        }

        /// <summary>
        /// Fallback-only sequence: show "select for contact info" 5s â†’ fade out â†’ slide back
        /// </summary>
        private async Task AnimateFallbackOnlyAsync(CancellationToken ct)
        {
            try
            {
                if (ct.IsCancellationRequested) return;

                ChatStatusText.Text = GetSelectForContactInfoText();
                AnimateSlideUp();
                AnimateFadeIn(ChatStatusText);

                await Task.Delay(5000, ct);
                if (ct.IsCancellationRequested) return;

                AnimateFadeOut(ChatStatusText);
                await Task.Delay(250, ct);
                if (ct.IsCancellationRequested) return;
                AnimateSlideBack();
            }
            catch (OperationCanceledException) { }
        }

        private string GetSelectForContactInfoText()
        {
            return _strings != null
                ? _strings.Get("ChatDetail_SelectForContactInfo", "select for contact info")
                : "select for contact info";
        }

        private void AnimateSlideUp()
        {
            var sb = new Storyboard();
            var anim = new DoubleAnimation
            {
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(200)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(anim, TitleTranslateTransform);
            Storyboard.SetTargetProperty(anim, "Y");
            sb.Children.Add(anim);
            sb.Begin();
        }

        private void AnimateSlideBack()
        {
            var sb = new Storyboard();
            var anim = new DoubleAnimation
            {
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(200)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(anim, TitleTranslateTransform);
            Storyboard.SetTargetProperty(anim, "Y");
            sb.Children.Add(anim);
            sb.Begin();
        }

        private void AnimateFadeIn(UIElement element)
        {
            element.Visibility = Visibility.Visible;
            var sb = new Storyboard();
            var anim = new DoubleAnimation
            {
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(200)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(anim, element);
            Storyboard.SetTargetProperty(anim, "Opacity");
            sb.Children.Add(anim);
            sb.Begin();
        }

        private void AnimateFadeOut(UIElement element)
        {
            var sb = new Storyboard();
            var anim = new DoubleAnimation
            {
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(200)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(anim, element);
            Storyboard.SetTargetProperty(anim, "Opacity");
            sb.Children.Add(anim);

            sb.Completed += (s, e) => {
                if (element.Opacity == 0)
                {
                    element.Visibility = Visibility.Collapsed;
                }
            };

            sb.Begin();
        }

        #endregion
    }
}
