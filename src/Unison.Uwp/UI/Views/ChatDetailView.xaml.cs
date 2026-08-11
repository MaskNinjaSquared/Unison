using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Contracts;
using Unison.Core.Factories;
using Unison.Core.Helpers;
using Unison.Core.Mappers;
using Unison.Core.Models;
using Unison.Core.ViewModels;
using Unison.Uwp.Helpers;
using Unison.Uwp.Services;
using Unison.Uwp.Services.WhatsApp;
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

        /// <summary>
        /// DI ViewModel owns composer, pin, audio prepare, presence watch, and timeline VMs.
        /// List chrome / MediaElement / Storyboards stay in code-behind.
        /// Loaded → InitializeAsync; Unloaded → UninitializeAsync.
        /// </summary>
        public ChatDetailViewModel ViewModel { get; private set; }

        private readonly IStringResources _strings;
        private readonly IChatMessageVmFactory _messageFactory;

        /// <summary>
        /// Concrete client for UWP-only helpers (ClearUnread, SchedulePersist, cast paths).
        /// </summary>
        private WhatsAppService WhatsApp =>
            App.GetWhatsAppService() as WhatsAppService ?? WhatsAppService.Instance;

        public bool HasActiveChat => ActiveChatGrid.Visibility == Visibility.Visible;

        private ScrollViewer _scrollViewer;
        private bool _isLoadingMore = false;
        private bool _hasReachedStart = false;
        private int _emptyLoadAttempts = 0;
        private bool _isSyncingFromService = false;
        private bool _syncRequestedAgain = false;
        private DateTime _suppressLoadMoreUntilUtc = DateTime.MinValue;
        private CancellationTokenSource _chatLoadCts;
        private bool _serviceEventsAttached;
        private const int MaxUiMessages = 300;
        private ChatMessageViewModel _displayedPinnedMessage;
        private List<ChatMessageViewModel> _activePinnedMessages = new List<ChatMessageViewModel>();
        private int _displayedPinnedIndex;
        private ChatMessage _playingAudioMessage;

        /// <summary>Cancels in-flight Storyboard sequences when presence watch restarts.</summary>
        private CancellationTokenSource _presenceAnimationCts;

        public ChatDetailView()
        {
            _strings = App.Services?.GetService<IStringResources>();
            _messageFactory = App.Services?.GetService<IChatMessageVmFactory>() ?? new ChatMessageVmFactory();

            if (App.Services != null)
            {
                ViewModel = App.Services.GetRequiredService<ChatDetailViewModel>();
                DataContext = ViewModel;
                ViewModel.BackRequested += (s, e) => BackRequested?.Invoke(this, e);
                ViewModel.MessageSent += (s, e) =>
                {
                    _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, ScrollToBottom);
                };
                ViewModel.MessagePinnedChanged += (s, e) =>
                {
                    _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, UpdatePinnedBanner);
                };
                ViewModel.PresenceAnimationRequested += ViewModel_PresenceAnimationRequested;
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
        }

        private void ChatDetailView_Loaded(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
            {
                _ = ViewModel.InitializeAsync();
            }

            if (!_serviceEventsAttached)
            {
                WhatsApp.OnChatMessagesChanged += WhatsAppService_OnChatMessagesChanged;
                _serviceEventsAttached = true;
            }
        }

        private void ChatDetailView_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_serviceEventsAttached)
            {
                WhatsApp.OnChatMessagesChanged -= WhatsAppService_OnChatMessagesChanged;
                _serviceEventsAttached = false;
            }

            _chatLoadCts?.Cancel();
            _chatLoadCts?.Dispose();
            _chatLoadCts = null;
            CancelPresenceAnimation();
            AudioPlayer.Stop();
            AudioPlayer.Source = null;
            _playingAudioMessage = null;
            if (ViewModel != null)
            {
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

        private async void ScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            if (_scrollViewer == null || _isLoadingMore || _hasReachedStart || _activeChat == null) return;
            if (DateTime.UtcNow < _suppressLoadMoreUntilUtc) return;

            // Debug log every 500ms or so to avoid spamming too much, but for now let's see more
            // Debug.WriteLine($"[ChatDetailView] Scroll: Offset={_scrollViewer.VerticalOffset}, Extent={_scrollViewer.ExtentHeight}, Viewport={_scrollViewer.ViewportHeight}");

            // When user scrolls near top
            // Exige conteudo realmente rolavel. Sem isso, uma conversa curta abre com
            // offset baixo, dispara "carregar mais" imediatamente, prepende mensagens
            // antigas e a tela nunca assenta no fim.
            bool temConteudoRolavel = _scrollViewer.ExtentHeight > (_scrollViewer.ViewportHeight * 1.5);

            if (temConteudoRolavel && _scrollViewer.VerticalOffset < 300)
            {
                Debug.WriteLine($"[ChatDetailView] TRIGGER HIT: Offset={_scrollViewer.VerticalOffset} < 300. Loading more...");
                await LoadMoreMessagesAsync();
            }
        }

        private async Task LoadMoreMessagesAsync()
        {
            if (_isLoadingMore || _activeChat == null) return;
            _isLoadingMore = true;

            try
            {
                string requestedJid = WhatsApp.GetCanonicalJid(_activeChat.JID);
                Debug.WriteLine($"[ChatDetailView] Loading more messages for {requestedJid}. Current: {_messages.Count}");

                double oldExtentHeight = _scrollViewer?.ExtentHeight ?? 0;
                double oldOffset = _scrollViewer?.VerticalOffset ?? 0;

                var moreMessages = await WhatsApp.LoadMoreMessagesAsync(requestedJid);
                if (_activeChat == null ||
                    !string.Equals(WhatsApp.GetCanonicalJid(_activeChat.JID), requestedJid, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (moreMessages != null && moreMessages.Count > 0)
                {
                    Debug.WriteLine($"[ChatDetailView] Received {moreMessages.Count} messages to prepend.");
                    _emptyLoadAttempts = 0;
                    bool isGroup = _activeChat.IsGroup ||
                        requestedJid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase);
                    // Insert at top in chronological order
                    for (int i = 0; i < moreMessages.Count; i++)
                    {
                        var more = moreMessages[i];
                        if (more != null && isGroup &&
                            (string.IsNullOrEmpty(more.RemoteJid) ||
                             !more.RemoteJid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase)))
                        {
                            more.RemoteJid = requestedJid;
                        }
                        _messages.Insert(i, ToVm(more));
                    }
                    RecomputeMessageRuns(_messages, isGroup);

                    // Force layout update to get new extent height
                    MessageListView.UpdateLayout();

                    // Adjust scroll position so it doesn't jump
                    double newExtentHeight = _scrollViewer?.ExtentHeight ?? oldExtentHeight;
                    double heightDiff = newExtentHeight - oldExtentHeight;
                    
                    Debug.WriteLine($"[ChatDetailView] Scroll stabilization: OldOffset={oldOffset}, HeightDiff={heightDiff}, NewTarget={oldOffset + heightDiff}");
                    
                    _scrollViewer?.ChangeView(null, oldOffset + heightDiff, null, true);
                }
                else
                {
                    bool requestedOnDemand = await WhatsApp.EnsureHistoryOnDemandAsync(requestedJid, 80);
                    bool pendingOnDemand = WhatsApp.IsHistoryOnDemandPending(requestedJid);

                    if (requestedOnDemand || pendingOnDemand)
                    {
                        _emptyLoadAttempts = 0;
                        _hasReachedStart = false;
                        Debug.WriteLine($"[ChatDetailView] Waiting for on-demand history for {_activeChat.JID} (requested={requestedOnDemand}, pending={pendingOnDemand})");
                    }
                    else
                    {
                        _emptyLoadAttempts++;
                        _hasReachedStart = _emptyLoadAttempts >= 2;
                        Debug.WriteLine($"[ChatDetailView] No more messages to load for {_activeChat.JID} (attempt={_emptyLoadAttempts}, reachedStart={_hasReachedStart})");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ChatDetailView] Error loading more messages: {ex.Message}");
            }
            finally
            {
                _isLoadingMore = false;
            }

        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (TryCloseImageViewer())
            {
                return;
            }

            BackRequested?.Invoke(this, EventArgs.Empty);
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

        private void OpenImageViewer(ChatMessageViewModel messageVm)
        {
            if (ImageViewerOverlay == null || App.Services == null)
            {
                return;
            }

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

        public async Task SetActiveChatAsync(ChatItem chat)
        {
            TryCloseImageViewer();

            _chatLoadCts?.Cancel();
            _chatLoadCts?.Dispose();
            _chatLoadCts = new CancellationTokenSource();
            var token = _chatLoadCts.Token;

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
            }

            if (_activeChat != null)
            {
                _activeChat.PropertyChanged -= ActiveChat_PropertyChanged;
            }

            _activeChat = chat;
            service.SetActiveChatJid(chat?.JID);
            ViewModel?.SyncActiveChat(chat);

            if (chat != null)
            {
                // PN/LID aliases can temporarily produce more than one row for the same
                // conversation. Clear all equivalent rows so the green unread indicator
                // cannot reappear from an alias after opening the chat.
                await service.ClearUnreadForChatAsync(chat.JID);
            }

            _hasReachedStart = false;
            _emptyLoadAttempts = 0;
            _isLoadingMore = false;
            CancelPresenceAnimation();

            if (chat == null)
            {
                _messages.Clear();
                ActiveChatGrid.Visibility = Visibility.Collapsed;
                EmptyStateGrid.Visibility = Visibility.Visible;
                PinnedMessageButton.Visibility = Visibility.Collapsed;
                _displayedPinnedMessage = null;
                _activePinnedMessages.Clear();
                _displayedPinnedIndex = 0;
                if (HeaderAvatar != null)
                {
                    HeaderAvatar.AvatarUrl = null;
                    HeaderAvatar.IsGroup = false;
                }
                return;
            }

            _activeChat.PropertyChanged += ActiveChat_PropertyChanged;
            ActiveChatGrid.Visibility = Visibility.Visible;
            EmptyStateGrid.Visibility = Visibility.Collapsed;

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

            _messages.Clear();
            string requestedJid = service.GetCanonicalJid(chat.JID);
            Debug.WriteLine($"[ChatDetailView] Loading messages for {requestedJid}");
            var messages = await service.LoadMessagesForChatAsync(requestedJid);

            if (token.IsCancellationRequested || _activeChat == null ||
                !string.Equals(service.GetCanonicalJid(_activeChat.JID), requestedJid, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var visibleMessages = (messages ?? new List<ChatMessage>())
                .Where(m => m != null)
                .Skip(Math.Max(0, (messages?.Count ?? 0) - MaxUiMessages))
                .ToList();

            // Ensure group JID is stamped so ShowGroupSenderName works for older persisted rows.
            bool activeIsGroup = chat.IsGroup ||
                requestedJid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase);
            if (activeIsGroup)
            {
                foreach (var msg in visibleMessages)
                {
                    if (msg == null) continue;
                    if (string.IsNullOrEmpty(msg.RemoteJid) ||
                        !msg.RemoteJid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase))
                    {
                        msg.RemoteJid = requestedJid;
                    }
                }
            }

            RecomputeMessageRuns(visibleMessages, activeIsGroup);
            foreach (var msg in visibleMessages)
            {
                _messages.Add(ToVm(msg));
            }
            UpdatePinnedBanner();

            if (visibleMessages.Count > 0)
            {
                var lastMsg = visibleMessages[visibleMessages.Count - 1];
                bool isGroup = chat.IsGroup ||
                    (chat.JID ?? string.Empty).EndsWith("@g.us", StringComparison.OrdinalIgnoreCase);
                string rawPreview = ChatPreviewNormalizer.FormatListPreview(lastMsg, isGroup);
                ChatPreviewNormalizer.Normalize(
                    rawPreview,
                    ChatPreviewNormalizer.InferKindFromMessage(lastMsg),
                    out var previewKind,
                    out var preview);

                DateTime loadedUtc = lastMsg.Timestamp.Kind == DateTimeKind.Utc
                    ? lastMsg.Timestamp
                    : lastMsg.Timestamp.ToUniversalTime();
                DateTime currentUtc = chat.LastMessageTimestampUtc.HasValue
                    ? (chat.LastMessageTimestampUtc.Value.Kind == DateTimeKind.Utc
                        ? chat.LastMessageTimestampUtc.Value
                        : chat.LastMessageTimestampUtc.Value.ToUniversalTime())
                    : DateTime.MinValue;

                if (lastMsg.Timestamp != DateTime.MinValue && loadedUtc >= currentUtc &&
                    (chat.LastMessage != preview || chat.LastMessageKind != previewKind || currentUtc == DateTime.MinValue))
                {
                    chat.LastMessage = preview;
                    chat.LastMessageKind = previewKind;
                    chat.LastMessageTimestampUtc = loadedUtc;
                    chat.Timestamp = WhatsAppMapper.FormatTimestamp(
                        lastMsg.Timestamp,
                        LocalizedStrings.Get("Common_Yesterday", "Yesterday"));
                    service.SchedulePersistPublic();
                }
            }

            ScrollToBottom();
            if (chat.IsPersonal)
            {
                ViewModel?.StopPresenceWatch();
            }
            else if (!_isWindowsMobile && ViewModel != null)
            {
                ViewModel.StartPresenceWatch(chat.JID);
            }
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
            else if (e.PropertyName == nameof(ChatItem.AvatarUrl) ||
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
                return;
            }

            bool isGroup = chat.IsGroup
                || (!string.IsNullOrWhiteSpace(chat.JID)
                    && chat.JID.IndexOf("@g.us", StringComparison.OrdinalIgnoreCase) >= 0);

            HeaderAvatar.IsGroup = isGroup;
            HeaderAvatar.AvatarUrl = chat.AvatarUrl;
        }

        /// <summary>
        /// Groups/direct use resolved labels; Personal uses <see cref="ChatItem.GetNameResolved"/>
        /// (marker via <see cref="IStringResources"/>) with optional Runs + subtitle.
        /// </summary>
        private void ApplyChatTitle(ChatItem chat, WhatsAppService service)
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

        private void ScrollToBottom()
        {
            if (_messages.Count == 0)
            {
                return;
            }

            _suppressLoadMoreUntilUtc = DateTime.UtcNow.AddMilliseconds(900);
            var last = _messages[_messages.Count - 1];
            MessageListView.ScrollIntoView(last, ScrollIntoViewAlignment.Leading);

            // One deferred correction is enough after layout. The previous eight-pass
            // loop repeatedly forced layout for more than two seconds on every message.
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, async () =>
            {
                await Task.Delay(160);
                if (_messages.Count == 0 || last != _messages[_messages.Count - 1])
                {
                    return;
                }

                MessageListView.ScrollIntoView(last, ScrollIntoViewAlignment.Leading);
                if (_scrollViewer != null)
                {
                    double target = Math.Max(0, _scrollViewer.ExtentHeight - _scrollViewer.ViewportHeight);
                    _scrollViewer.ChangeView(null, target, null, true);
                }
            });
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
            if (messages == null || messages.Count == 0)
            {
                return;
            }

            var service = WhatsApp;

            for (int i = 0; i < messages.Count; i++)
            {
                var current = messages[i];
                if (current == null) continue;

                if (isGroup)
                {
                    EnsureGroupSenderName(current, service);
                }

                bool isRunStart = i == 0;
                bool isRunEnd = i == messages.Count - 1;

                if (!isRunStart)
                {
                    var prev = messages[i - 1];
                    isRunStart = !IsSameMessageRun(prev, current);
                }

                if (!isRunEnd)
                {
                    var next = messages[i + 1];
                    isRunEnd = !IsSameMessageRun(current, next);
                }

                current.IsRunStart = isRunStart;
                current.IsRunEnd = isRunEnd;
                current.ShowGroupSenderName =
                    isGroup &&
                    isRunStart &&
                    !current.IsFromMe &&
                    !string.IsNullOrWhiteSpace(current.SenderName) &&
                    !string.Equals(current.SenderName, "Me", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(current.SenderName, "You", StringComparison.OrdinalIgnoreCase);
            }
        }

        private static void EnsureGroupSenderName(ChatMessage message, WhatsAppService service)
        {
            if (message == null || message.IsFromMe)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(message.SenderName) &&
                !string.Equals(message.SenderName, "Me", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(message.SenderName, "You", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string participant = message.ParticipantJid;
            if (string.IsNullOrWhiteSpace(participant))
            {
                return;
            }

            string resolved = service?.ResolveDisplayName(participant, "sender");
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                message.SenderName = resolved;
            }
        }

        private static bool IsSameMessageRun(ChatMessage left, ChatMessage right)
        {
            if (left == null || right == null)
            {
                return false;
            }

            if (left.IsFromMe != right.IsFromMe)
            {
                return false;
            }

            // Own bubbles stay grouped; received group bubbles break when the participant changes.
            if (left.IsFromMe)
            {
                return true;
            }

            string leftParticipant = left.ParticipantJid ?? string.Empty;
            string rightParticipant = right.ParticipantJid ?? string.Empty;
            if (!string.IsNullOrEmpty(leftParticipant) && !string.IsNullOrEmpty(rightParticipant))
            {
                return string.Equals(leftParticipant, rightParticipant, StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(left.SenderName ?? string.Empty, right.SenderName ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        private void WhatsAppService_OnChatMessagesChanged(object sender, string updatedJid)
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
                bool wasNearBottom = IsNearBottom();
                var serviceMessages = await service.LoadMessagesForChatAsync(requestedJid);
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

                    bool changed = false;
                    var existingIds = new HashSet<string>(_messages
                        .Where(m => m != null && !string.IsNullOrWhiteSpace(m.Id))
                        .Select(m => m.Id));

                    for (int i = 0; i < serviceMessages.Count; i++)
                    {
                        var msg = serviceMessages[i];
                        if (msg == null)
                        {
                            continue;
                        }

                        bool alreadyExists = !string.IsNullOrWhiteSpace(msg.Id)
                            ? existingIds.Contains(msg.Id)
                            : _messages.Any(m =>
                                m != null &&
                                string.IsNullOrWhiteSpace(m.Id) &&
                                m.Timestamp == msg.Timestamp &&
                                m.IsFromMe == msg.IsFromMe &&
                                string.Equals(m.Content, msg.Content, StringComparison.Ordinal));

                        if (alreadyExists)
                        {
                            var existing = !string.IsNullOrWhiteSpace(msg.Id)
                                ? _messages.FirstOrDefault(m => string.Equals(m?.Id, msg.Id, StringComparison.Ordinal))
                                : null;
                            if (existing?.Model != null)
                            {
                                existing.Model.Status = msg.Status;
                                existing.Model.IsPinned = msg.IsPinned;
                                existing.Model.PinnedAtUtc = msg.PinnedAtUtc;
                                existing.Model.PinExpiresAtUtc = msg.PinExpiresAtUtc;
                                existing.Model.RemoteJid = msg.RemoteJid;
                                existing.Model.ParticipantJid = msg.ParticipantJid;
                                existing.Model.Reactions = msg.Reactions;
                                if (!string.IsNullOrWhiteSpace(msg.ImageUri))
                                {
                                    existing.Model.ImageUri = msg.ImageUri;
                                }
                                if (!string.IsNullOrWhiteSpace(msg.AudioUri))
                                {
                                    existing.Model.AudioUri = msg.AudioUri;
                                }
                            }
                            continue;
                        }

                        if (_activeChat != null &&
                            (_activeChat.IsGroup || requestedJid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase)) &&
                            (string.IsNullOrEmpty(msg.RemoteJid) ||
                             !msg.RemoteJid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase)))
                        {
                            msg.RemoteJid = requestedJid;
                        }

                        if (i <= _messages.Count)
                        {
                            _messages.Insert(i, ToVm(msg));
                        }
                        else
                        {
                            _messages.Add(ToVm(msg));
                        }

                        if (!string.IsNullOrWhiteSpace(msg.Id))
                        {
                            existingIds.Add(msg.Id);
                        }
                        changed = true;
                    }

                    UpdatePinnedBanner();

                    while (_messages.Count > MaxUiMessages)
                    {
                        _messages.RemoveAt(0);
                    }

                    if (changed)
                    {
                        bool isGroup = _activeChat.IsGroup ||
                            requestedJid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase);
                        RecomputeMessageRuns(_messages, isGroup);
                        if (wasNearBottom)
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
            if (e.Key != Windows.System.VirtualKey.Enter || ViewModel?.SendMessageCommand == null)
            {
                return;
            }

            if (!ViewModel.SendMessageCommand.CanExecute(null))
            {
                return;
            }

            e.Handled = true;
            ViewModel.SendMessageCommand.Execute(null);
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
                ? "[MÃ­dia]"
                : preview.Replace("\r", " ").Replace("\n", " ");
            PinnedMessageTitleText.Text = _activePinnedMessages.Count > 1
                ? LocalizedStrings.Format("ChatDetail_PinnedIndex", _displayedPinnedIndex + 1, _activePinnedMessages.Count)
                : LocalizedStrings.Get("ChatDetail_Pinned.Text");
            PinnedMessageButton.Visibility = Visibility.Visible;
        }

        private void PinnedMessageButton_Click(object sender, RoutedEventArgs e)
        {
            if (_displayedPinnedMessage == null) return;

            MessageListView.ScrollIntoView(_displayedPinnedMessage, ScrollIntoViewAlignment.Leading);
            if (_activePinnedMessages.Count > 1)
            {
                _displayedPinnedIndex = (_displayedPinnedIndex + 1) % _activePinnedMessages.Count;
                ShowPinnedBannerItem();
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
            var message = UnwrapMessage(anchor?.DataContext);
            if (message == null || _activeChat == null || string.IsNullOrWhiteSpace(message.Id)) return;

            var flyout = new MenuFlyout();
            if (message.IsPinned)
            {
                AddPinAction(flyout, message, "Desafixar mensagem", false, 0);
            }
            else
            {
                AddPinAction(flyout, message, "Fixar por 24 horas", true, 86400);
                AddPinAction(flyout, message, "Fixar por 7 dias", true, 604800);
                AddPinAction(flyout, message, "Fixar por 30 dias", true, 2592000);
            }
            // The one-argument FlyoutBase.ShowAt overload requires Windows 10 1809.
            // Windows 10 Mobile 15063 supports the original MenuFlyout overload.
            flyout.ShowAt(anchor, new Windows.Foundation.Point(
                Math.Max(0, anchor.ActualWidth / 2),
                Math.Max(0, anchor.ActualHeight / 2)));
        }

        private void AddPinAction(MenuFlyout flyout, ChatMessage message, string label, bool pin, uint durationSeconds)
        {
            var item = new MenuFlyoutItem { Text = label };
            item.Click += async (s, e) =>
            {
                if (ViewModel == null) return;
                await ViewModel.SetMessagePinnedAsync(message, pin, durationSeconds);
                UpdatePinnedBanner();
            };
            flyout.Items.Add(item);
        }

        /// <summary>Invoked from <see cref="Templates.MessageTemplates"/> (external DataTemplate events).</summary>
        internal async void OnAudioButtonClick(object sender, RoutedEventArgs e)
        {
            var message = UnwrapMessage((sender as FrameworkElement)?.DataContext);
            if (message == null || !message.IsAudio || ViewModel == null) return;

            try
            {
                if (_playingAudioMessage != null &&
                    string.Equals(_playingAudioMessage.Id, message.Id, StringComparison.Ordinal) &&
                    AudioPlayer.CurrentState == MediaElementState.Playing)
                {
                    AudioPlayer.Pause();
                    return;
                }

                string uri = await ViewModel.EnsureAudioReadyAsync(message);
                if (string.IsNullOrWhiteSpace(uri)) return;

                AudioPlayer.Stop();
                AudioPlayer.Source = new Uri(uri);
                _playingAudioMessage = message;
                AudioPlayer.Play();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(string.Format("[ChatDetailView] Audio playback failed: {0}", ex));
            }
        }

        /// <summary>Invoked from <see cref="Templates.MessageTemplates"/> image download overlay.</summary>
        internal async void OnImageDownloadButtonClick(object sender, RoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            var vm = element?.DataContext as ChatMessageViewModel;
            var message = vm?.Model ?? UnwrapMessage(element?.DataContext);
            if (message == null || !message.NeedsImageDownload || ViewModel == null) return;

            if (vm != null)
            {
                vm.IsImageDownloading = true;
            }

            try
            {
                await ViewModel.EnsureImageReadyAsync(message);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(string.Format("[ChatDetailView] Image download failed: {0}", ex));
            }
            finally
            {
                if (vm != null)
                {
                    vm.IsImageDownloading = false;
                }
            }
        }

        private void AudioPlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            _playingAudioMessage = null;
        }

        private void AudioPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            Debug.WriteLine(string.Format("[ChatDetailView] MediaElement failed: {0}", e.ErrorMessage));
            _playingAudioMessage = null;
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

                if (!string.IsNullOrEmpty(statusText))
                {
                    await AnimateStatusSequenceAsync(statusText, ct);
                }
                else
                {
                    await AnimateFallbackOnlyAsync(ct);
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
        /// Fallback-only sequence: show "select for contact info" 5s → fade out → slide back
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
