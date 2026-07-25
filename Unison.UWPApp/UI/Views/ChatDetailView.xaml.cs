using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using Unison.UWPApp.Client;
using Unison.UWPApp.Models;
using Unison.UWPApp.Services;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media.Animation;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Linq;

namespace Unison.UWPApp.UI.Views
{
    public sealed partial class ChatDetailView : UserControl
    {
        private ChatItem _activeChat;
        private ObservableCollection<ChatMessage> _messages = new ObservableCollection<ChatMessage>();
        public event EventHandler BackRequested;

        public bool HasActiveChat => ActiveChatGrid.Visibility == Visibility.Visible;

        private ScrollViewer _scrollViewer;
        private bool _isLoadingMore = false;
        private bool _hasReachedStart = false;
        private int _emptyLoadAttempts = 0;
        private bool _isSyncingFromService = false;
        private bool _isSendingMessage = false;
        private DateTime _suppressLoadMoreUntilUtc = DateTime.MinValue;

        // Presence animation state
        private CancellationTokenSource _presenceCts;
        private DateTime _chatOpenedTime;
        private bool _presenceReceived;
        private string _pendingPresenceText;

        public ChatDetailView()
        {
            this.InitializeComponent();
            MessageListView.ItemsSource = _messages;
            MessageListView.Loaded += MessageListView_Loaded;
            this.Unloaded += ChatDetailView_Unloaded;
            WhatsAppService.Instance.OnChatMessagesChanged += WhatsAppService_OnChatMessagesChanged;
        }

        private void ChatDetailView_Unloaded(object sender, RoutedEventArgs e)
        {
            WhatsAppService.Instance.OnChatMessagesChanged -= WhatsAppService_OnChatMessagesChanged;
        }

        private void MessageListView_Loaded(object sender, RoutedEventArgs e)
        {
            _scrollViewer = FindScrollViewer(MessageListView);
            if (_scrollViewer != null)
            {
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
            if (_scrollViewer.VerticalOffset < 300)
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
                Debug.WriteLine($"[ChatDetailView] Loading more messages for {_activeChat.JID}. Current: {_messages.Count}");
                
                double oldExtentHeight = _scrollViewer.ExtentHeight;
                double oldOffset = _scrollViewer.VerticalOffset;

                var moreMessages = await WhatsAppService.Instance.LoadMoreMessagesAsync(_activeChat.JID);
                
                if (moreMessages != null && moreMessages.Count > 0)
                {
                    Debug.WriteLine($"[ChatDetailView] Received {moreMessages.Count} messages to prepend.");
                    _emptyLoadAttempts = 0;
                    // Insert at top in chronological order
                    for (int i = 0; i < moreMessages.Count; i++)
                    {
                        _messages.Insert(i, moreMessages[i]);
                    }
                    RecomputeMessageRuns(_messages);

                    // Force layout update to get new extent height
                    MessageListView.UpdateLayout();

                    // Adjust scroll position so it doesn't jump
                    double newExtentHeight = _scrollViewer.ExtentHeight;
                    double heightDiff = newExtentHeight - oldExtentHeight;
                    
                    Debug.WriteLine($"[ChatDetailView] Scroll stabilization: OldOffset={oldOffset}, HeightDiff={heightDiff}, NewTarget={oldOffset + heightDiff}");
                    
                    _scrollViewer.ChangeView(null, oldOffset + heightDiff, null, true);
                }
                else
                {
                    bool requestedOnDemand = await WhatsAppService.Instance.EnsureHistoryOnDemandAsync(_activeChat.JID, 80);
                    bool pendingOnDemand = WhatsAppService.Instance.IsHistoryOnDemandPending(_activeChat.JID);

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

            if (!_hasReachedStart && _scrollViewer != null && _scrollViewer.VerticalOffset < 300)
            {
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, async () => {
                    await Task.Delay(800); // Give layout more time
                    if (!_isLoadingMore && !_hasReachedStart && _scrollViewer.VerticalOffset < 300)
                    {
                        Debug.WriteLine($"[ChatDetailView] AUTO-RETRIGGER: Still near top (Offset={_scrollViewer.VerticalOffset}). Loading another batch.");
                        await LoadMoreMessagesAsync();
                    }
                });
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            BackRequested?.Invoke(this, EventArgs.Empty);
        }

        public async void SetActiveChat(ChatItem chat)
        {
            var service = WhatsAppService.Instance;
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
            _hasReachedStart = false; // Reset for new chat
            _emptyLoadAttempts = 0;

            // Cancel any running presence animation from previous chat
            CancelPresenceAnimation();

            if (chat == null)
            {
                ActiveChatGrid.Visibility = Visibility.Collapsed;
                EmptyStateGrid.Visibility = Visibility.Visible;
                return;
            }

            _activeChat.PropertyChanged += ActiveChat_PropertyChanged;

            ActiveChatGrid.Visibility = Visibility.Visible;
            EmptyStateGrid.Visibility = Visibility.Collapsed;
            ChatTitleText.Text = WhatsAppService.Instance.ResolveDisplayName(chat.JID, "header");

            // Reset status text
            ChatStatusText.Text = "";
            ChatStatusText.Opacity = 0;
            ChatStatusText.Visibility = Visibility.Collapsed;
            TitleTranslateTransform.Y = 0;

            // Ensure we have the scrollviewer
            if (_scrollViewer == null)
            {
                _scrollViewer = FindScrollViewer(MessageListView);
                if (_scrollViewer != null)
                {
                    _scrollViewer.ViewChanged += ScrollViewer_ViewChanged;
                    Debug.WriteLine("[ChatDetailView] Found ScrollViewer during SetActiveChat");
                }
            }

            // Set avatar
            if (!string.IsNullOrEmpty(chat.AvatarUrl))
            {
                // Show profile picture
                AvatarImageBrush.ImageSource = new Windows.UI.Xaml.Media.Imaging.BitmapImage(new Uri(chat.AvatarUrl));
                AvatarImageEllipse.Visibility = Visibility.Visible;
                AvatarFallbackEllipse.Visibility = Visibility.Collapsed;
                AvatarInitialText.Visibility = Visibility.Collapsed;
            }
            else
            {
                // Show fallback initial
                AvatarInitialText.Text = chat.Initial;
                AvatarImageEllipse.Visibility = Visibility.Collapsed;
                AvatarFallbackEllipse.Visibility = Visibility.Visible;
                AvatarInitialText.Visibility = Visibility.Visible;
            }

            // Load messages from disk if not already in memory
            _messages.Clear();
            Debug.WriteLine($"[ChatDetailView] Loading messages for {chat.JID}");
            var messages = await WhatsAppService.Instance.LoadMessagesForChatAsync(chat.JID);
            Debug.WriteLine($"[ChatDetailView] Loaded {messages.Count} messages for {chat.JID}");
            RecomputeMessageRuns(messages);
            
            foreach (var msg in messages)
            {
                _messages.Add(msg);
            }

            // Trigger one background history-on-demand request for this chat on open.
            // This mirrors WhatsApp Web's explicit "get older messages" behavior without requiring a top scroll first.
            if (messages.Count > 0)
            {
                _ = WhatsAppService.Instance.EnsureHistoryOnDemandAsync(chat.JID, 80);
                _ = WhatsAppService.Instance.EnsureActiveChatReconciledAsync(chat.JID);
            }

            // Sync chat preview with actual last message
            if (messages.Count > 0)
            {
                var lastMsg = messages[messages.Count - 1];
                var previewContent = lastMsg.Content ?? "[Media]";
                
                // Format preview (truncate, remove line breaks)
                var preview = previewContent.Length > 50 ? previewContent.Substring(0, 50) + "..." : previewContent;
                preview = preview.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
                
                if (chat.LastMessage != preview)
                {
                    chat.LastMessage = preview;
                    // Format timestamp: Today shows time, otherwise shows date
                    var msgDate = lastMsg.Timestamp.Date;
                    var today = DateTime.Today;
                    if (msgDate == today)
                        chat.Timestamp = lastMsg.Timestamp.ToString("HH:mm");
                    else if (msgDate == today.AddDays(-1))
                        chat.Timestamp = "Yesterday";
                    else if (msgDate > today.AddDays(-7))
                        chat.Timestamp = lastMsg.Timestamp.ToString("dddd");
                    else
                        chat.Timestamp = lastMsg.Timestamp.ToString("dd/MM/yyyy");
                    Debug.WriteLine($"[ChatDetailView] Updated preview to: {preview}");
                    
                    // Persist the updated chat metadata to disk
                    WhatsAppService.Instance.SchedulePersistPublic();
                }
            }

            ScrollToBottom();

            // Start presence subscription & animation sequence
            StartPresenceSequence(chat.JID);
        }

        private void ActiveChat_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ChatItem.Name) && _activeChat != null)
            {
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    ChatTitleText.Text = WhatsAppService.Instance.ResolveDisplayName(_activeChat.JID, "header");
                });
            }
        }

        private void ScrollToBottom()
        {
            if (_messages.Count > 0)
            {
                // Guard against load-more while we are programmatically settling at the bottom.
                _suppressLoadMoreUntilUtc = DateTime.UtcNow.AddMilliseconds(1200);

                MessageListView.ScrollIntoView(_messages[_messages.Count - 1]);
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, async () =>
                {
                    await Task.Delay(40);
                    ForceScrollViewerToBottom("pass1");
                    await Task.Delay(140);
                    ForceScrollViewerToBottom("pass2");
                });
            }
        }

        private static void RecomputeMessageRuns(IList<ChatMessage> messages)
        {
            if (messages == null || messages.Count == 0)
            {
                return;
            }

            for (int i = 0; i < messages.Count; i++)
            {
                var current = messages[i];
                if (current == null) continue;

                bool isRunStart = i == 0;
                bool isRunEnd = i == messages.Count - 1;

                if (!isRunStart)
                {
                    var prev = messages[i - 1];
                    isRunStart = prev == null || prev.IsFromMe != current.IsFromMe;
                }

                if (!isRunEnd)
                {
                    var next = messages[i + 1];
                    isRunEnd = next == null || next.IsFromMe != current.IsFromMe;
                }

                current.IsRunStart = isRunStart;
                current.IsRunEnd = isRunEnd;
            }
        }

        private void ForceScrollViewerToBottom(string reason)
        {
            if (_scrollViewer == null) return;

            MessageListView.UpdateLayout();
            double target = Math.Max(0, _scrollViewer.ExtentHeight - _scrollViewer.ViewportHeight);
            if (Math.Abs(_scrollViewer.VerticalOffset - target) > 1.0)
            {
                Debug.WriteLine($"[ChatDetailView] ForceScrollViewerToBottom ({reason}): {Math.Round(_scrollViewer.VerticalOffset, 1)} -> {Math.Round(target, 1)}");
                _scrollViewer.ChangeView(null, target, null, true);
            }
        }

        private async void WhatsAppService_OnChatMessagesChanged(object sender, string updatedJid)
        {
            if (_activeChat == null || string.IsNullOrWhiteSpace(updatedJid))
            {
                return;
            }

            var service = WhatsAppService.Instance;
            string activeCanonical = service.GetCanonicalJid(_activeChat.JID);
            string updatedCanonical = service.GetCanonicalJid(updatedJid);
            if (!string.Equals(activeCanonical, updatedCanonical, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await SyncMessagesFromServiceAsync();
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
            try
            {
                bool wasNearBottom = IsNearBottom();
                var serviceMessages = await WhatsAppService.Instance.LoadMessagesForChatAsync(_activeChat.JID);
                if (serviceMessages == null || serviceMessages.Count == 0)
                {
                    return;
                }

                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    bool changed = false;
                    var existingIds = new HashSet<string>(_messages.Where(m => m != null && !string.IsNullOrWhiteSpace(m.Id)).Select(m => m.Id));

                    for (int i = 0; i < serviceMessages.Count; i++)
                    {
                        var msg = serviceMessages[i];
                        if (msg == null)
                        {
                            continue;
                        }

                        bool alreadyExists;
                        if (!string.IsNullOrWhiteSpace(msg.Id))
                        {
                            alreadyExists = existingIds.Contains(msg.Id);
                        }
                        else
                        {
                            alreadyExists = _messages.Any(m =>
                                m != null &&
                                string.IsNullOrWhiteSpace(m.Id) &&
                                m.Timestamp == msg.Timestamp &&
                                m.IsFromMe == msg.IsFromMe &&
                                string.Equals(m.Content, msg.Content, StringComparison.Ordinal));
                        }

                        if (alreadyExists)
                        {
                            continue;
                        }

                        if (i >= 0 && i <= _messages.Count)
                        {
                            _messages.Insert(i, msg);
                        }
                        else
                        {
                            _messages.Add(msg);
                        }

                        if (!string.IsNullOrWhiteSpace(msg.Id))
                        {
                            existingIds.Add(msg.Id);
                        }
                        changed = true;
                    }

                    if (changed && wasNearBottom)
                    {
                        ScrollToBottom();
                    }

                    if (changed)
                    {
                        RecomputeMessageRuns(_messages);
                    }
                });

                await WhatsAppService.Instance.EnsureActiveChatReconciledAsync(_activeChat.JID);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ChatDetailView] SyncMessagesFromServiceAsync failed: {ex.Message}");
            }
            finally
            {
                _isSyncingFromService = false;
            }
        }

        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            SendMessage();
        }

        private void MessageInput_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                SendMessage();
            }
        }

        private async void SendMessage()
        {
            string text = MessageInput.Text;
            if (_isSendingMessage || string.IsNullOrWhiteSpace(text) || _activeChat == null) return;

            // Clear input immediately for responsiveness
            MessageInput.Text = "";
            _isSendingMessage = true;
            SendButton.IsEnabled = false;

            try
            {
                // Send via WhatsApp service
                var msg = await WhatsAppService.Instance.SendTextMessageAsync(_activeChat.JID, text);
                
                // Add to local UI
                _messages.Add(msg);
                RecomputeMessageRuns(_messages);
                ScrollToBottom();
            }
            catch (Exception ex)
            {
                // Show error, restore the text so user can try again
                System.Diagnostics.Debug.WriteLine($"[ChatDetailView] Send failed: {ex.Message}");
                MessageInput.Text = text;
                
                // Could show a dialog or toast here
                // For now, just log the error
            }
            finally
            {
                _isSendingMessage = false;
                SendButton.IsEnabled = true;
            }
        }
        private async void AttachButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                picker.ViewMode = Windows.Storage.Pickers.PickerViewMode.Thumbnail;
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".png");

                var file = await picker.PickSingleFileAsync();
                if (file != null)
                {
                    // 1. Read file bytes first
                    byte[] fileBytes;
                    using (var stream = await file.OpenReadAsync())
                    {
                        fileBytes = new byte[stream.Size];
                        using (var reader = new Windows.Storage.Streams.DataReader(stream))
                        {
                            await reader.LoadAsync((uint)stream.Size);
                            reader.ReadBytes(fileBytes);
                        }
                    }

                    // 2. Create preview from bytes (separate stream for bitmap)
                    var bitmap = new Windows.UI.Xaml.Media.Imaging.BitmapImage();
                    using (var memStream = new Windows.Storage.Streams.InMemoryRandomAccessStream())
                    {
                        await memStream.WriteAsync(fileBytes.AsBuffer());
                        memStream.Seek(0);
                        await bitmap.SetSourceAsync(memStream);
                    }
                    
                    PreviewImage.Source = bitmap;
                    ImageInfoText.Text = $"{file.Name} ({fileBytes.Length / 1024} KB)";

                    // 3. Confirm Send
                    var result = await ImagePreviewDialog.ShowAsync();
                    if (result == ContentDialogResult.Primary)
                    {
                        if (_activeChat != null)
                        {
                            string caption = MessageInput.Text?.Trim();
                            if (string.IsNullOrEmpty(caption))
                            {
                                caption = null;
                            }

                            var sent = await WhatsAppService.Instance.SendImageMessageAsync(_activeChat.JID, fileBytes, caption);
                            _messages.Add(sent);
                            RecomputeMessageRuns(_messages);

                            // Caption is consumed by the image message send path.
                            if (caption != null)
                            {
                                MessageInput.Text = "";
                            }

                            ScrollToBottom();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ChatView] Attach/Send Error: {ex}");
            }
        }
        #region Presence Animation

        private void CancelPresenceAnimation()
        {
            _presenceCts?.Cancel();
            _presenceCts?.Dispose();
            _presenceCts = null;
            _presenceReceived = false;
            _pendingPresenceText = null;

            // Unhook event
            var socket = WhatsAppService.Instance?.Socket;
            if (socket != null)
            {
                socket.OnPresenceUpdate -= Socket_OnPresenceUpdate;
            }
        }

        private void StartPresenceSequence(string jid)
        {
            if (string.IsNullOrEmpty(jid)) return;

            _presenceCts = new CancellationTokenSource();
            _chatOpenedTime = DateTime.UtcNow;
            _presenceReceived = false;
            _pendingPresenceText = null;

            // Groups don't have individual presence — show group info fallback directly
            if (jid.Contains("@g.us"))
            {
                Debug.WriteLine("[ChatDetailView] Group chat detected, showing group info text");
                _presenceReceived = true;
                _pendingPresenceText = "select here for group info";
                _ = RunPresenceTimerAsync(_presenceCts.Token, null);
                return;
            }

            var socket = WhatsAppService.Instance?.Socket;
            if (socket != null)
            {
                socket.OnPresenceUpdate += Socket_OnPresenceUpdate;
            }

            // Start the timer — it will attempt the subscribe inside the loop,
            // retrying if the socket isn't connected yet
            _ = RunPresenceTimerAsync(_presenceCts.Token, jid);
        }

        private async void Socket_OnPresenceUpdate(object sender, PresenceUpdateEventArgs e)
        {
            if (_activeChat == null || _presenceCts == null || _presenceCts.IsCancellationRequested) return;

            // Accept any presence update — server may respond with a different internal
            // device JID (e.g. "167495184957535.1:0@s.whatsapp.net") than the phone
            // number we subscribed to. Since we only subscribe to one user at a time,
            // any response within the timer window belongs to our subscribed user.
            _presenceReceived = true;
            _pendingPresenceText = FormatPresenceText(e.Presence, e.LastSeen);

            Debug.WriteLine($"[ChatDetailView] Presence received for {e.Jid}: {_pendingPresenceText}");
        }

        private async Task RunPresenceTimerAsync(CancellationToken ct, string subscribeJid)
        {
            try
            {
                bool subscribed = false;

                // Wait up to 3 seconds for presence data
                for (int i = 0; i < 30; i++)
                {
                    if (ct.IsCancellationRequested) return;
                    if (_presenceReceived) break;

                    // Try to subscribe if we haven't yet and have a JID
                    if (!subscribed && !string.IsNullOrEmpty(subscribeJid))
                    {
                        var socket = WhatsAppService.Instance?.Socket;
                        if (socket != null && socket.IsConnected)
                        {
                            Debug.WriteLine($"[ChatDetailView] Socket now connected, subscribing to presence for {subscribeJid}");
                            socket.OnPresenceUpdate -= Socket_OnPresenceUpdate;
                            socket.OnPresenceUpdate += Socket_OnPresenceUpdate;
                            _ = socket.PresenceSubscribeAsync(subscribeJid);
                            subscribed = true;
                        }
                    }

                    await Task.Delay(100, ct);
                }

                if (ct.IsCancellationRequested) return;

                if (_presenceReceived && !string.IsNullOrEmpty(_pendingPresenceText))
                {
                    // Calculate remaining delay if < 3s
                    var elapsed = (DateTime.UtcNow - _chatOpenedTime).TotalMilliseconds;
                    if (elapsed < 3000)
                    {
                        await Task.Delay((int)(3000 - elapsed), ct);
                    }

                    if (ct.IsCancellationRequested) return;

                    await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
                    {
                        try
                        {
                            if (ct.IsCancellationRequested) return;
                            await AnimateStatusSequenceAsync(_pendingPresenceText, ct);
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[ChatDetailView] Status animation lambda error: {ex.Message}");
                        }
                    });
                }
                else
                {
                    // No presence — run fallback
                    await RunFallbackSequenceAsync(ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ChatDetailView] Presence timer error: {ex.Message}");
            }
        }

        private async Task RunFallbackSequenceAsync(CancellationToken ct)
        {
            try
            {
                // Wait 3 seconds before showing fallback
                var elapsed = (DateTime.UtcNow - _chatOpenedTime).TotalMilliseconds;
                if (elapsed < 3000)
                {
                    await Task.Delay((int)(3000 - elapsed), ct);
                }

                if (ct.IsCancellationRequested) return;

                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
                {
                    try
                    {
                        if (ct.IsCancellationRequested) return;
                        await AnimateFallbackOnlyAsync(ct);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ChatDetailView] Fallback animation lambda error: {ex.Message}");
                    }
                });
            }
            catch (OperationCanceledException) { }
        }

        /// <summary>
        /// Full sequence: show presence status 5s → crossfade to "select for contact info" 5s → fade out → slide back
        /// </summary>
        private async Task AnimateStatusSequenceAsync(string statusText, CancellationToken ct)
        {
            try
            {
                if (ct.IsCancellationRequested) return;

                // Phase 1: Slide name up and fade in status
                ChatStatusText.Text = statusText;
                AnimateSlideUp();
                AnimateFadeIn(ChatStatusText);

                // Hold for 5 seconds
                await Task.Delay(5000, ct);
                if (ct.IsCancellationRequested) return;

                // Phase 2: Crossfade to "select for contact info"
                AnimateFadeOut(ChatStatusText);
                await Task.Delay(250, ct); // Wait for fade out
                if (ct.IsCancellationRequested) return;

                ChatStatusText.Text = "select for contact info";
                AnimateFadeIn(ChatStatusText);

                // Hold for 5 seconds
                await Task.Delay(5000, ct);
                if (ct.IsCancellationRequested) return;

                // Phase 3: Fade out and slide back
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

                ChatStatusText.Text = "select for contact info";
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

        private string FormatPresenceText(string presence, long? lastSeenEpoch)
        {
            if (presence == "available" || presence == "composing")
            {
                return presence == "composing" ? "typing..." : "online";
            }

            if (lastSeenEpoch.HasValue && lastSeenEpoch.Value > 0)
            {
                var lastSeenUtc = DateTimeOffset.FromUnixTimeSeconds(lastSeenEpoch.Value);
                var lastSeenLocal = lastSeenUtc.LocalDateTime;
                var now = DateTime.Now;
                var timeStr = lastSeenLocal.ToString("HH:mm");

                if (lastSeenLocal.Date == now.Date)
                {
                    return $"last seen today at {timeStr}";
                }
                else if (lastSeenLocal.Date == now.Date.AddDays(-1))
                {
                    return $"last seen yesterday at {timeStr}";
                }
                else if (lastSeenLocal.Date > now.Date.AddDays(-7))
                {
                    return $"last seen on {lastSeenLocal:dddd} at {timeStr}";
                }
                else
                {
                    return $"last seen {lastSeenLocal:dd/MM/yyyy} at {timeStr}";
                }
            }

            return "last seen recently";
        }

        #endregion
    }
}
