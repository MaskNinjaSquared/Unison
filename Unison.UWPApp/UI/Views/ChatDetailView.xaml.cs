using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using Unison.UWPApp.Models;
using Unison.UWPApp.Services;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using System.Diagnostics;
using System.Threading.Tasks;

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

        public ChatDetailView()
        {
            this.InitializeComponent();
            MessageListView.ItemsSource = _messages;
            MessageListView.Loaded += MessageListView_Loaded;
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

            // When user scrolls to top (offset close to 200)
            if (_scrollViewer.VerticalOffset < 200)
            {
                Debug.WriteLine($"[ChatDetailView] Scroll trigger hit. Offset: {_scrollViewer.VerticalOffset}, Extent: {_scrollViewer.ExtentHeight}, Viewport: {_scrollViewer.ViewportHeight}");
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
                    // Insert at top in chronological order
                    for (int i = 0; i < moreMessages.Count; i++)
                    {
                        _messages.Insert(i, moreMessages[i]);
                    }

                    // Force layout update to get new extent height
                    MessageListView.UpdateLayout();

                    // Adjust scroll position so it doesn't jump
                    double newExtentHeight = _scrollViewer.ExtentHeight;
                    double heightDiff = newExtentHeight - oldExtentHeight;
                    
                    Debug.WriteLine($"[ChatDetailView] Loaded {moreMessages.Count} messages. Height: {oldExtentHeight}->{newExtentHeight}, Diff: {heightDiff}. Updating offset: {oldOffset}->{oldOffset + heightDiff}");
                    
                    _scrollViewer.ChangeView(null, oldOffset + heightDiff, null, true);
                }
                else
                {
                    Debug.WriteLine($"[ChatDetailView] No more messages to load for {_activeChat.JID}");
                    _hasReachedStart = true;
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

            // If we are STILL near the top after loading more, and haven't reached start, try again
            // This handles cases where the prepended messages don't push the scroll offset far enough
            if (!_hasReachedStart && _scrollViewer != null && _scrollViewer.VerticalOffset < 200)
            {
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, async () => {
                    await Task.Delay(500); // Give layout a moment
                    if (!_isLoadingMore && !_hasReachedStart && _scrollViewer.VerticalOffset < 200)
                    {
                        Debug.WriteLine("[ChatDetailView] Auto-triggering another load (still near top)");
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
            _activeChat = chat;
            _hasReachedStart = false; // Reset for new chat
            if (chat == null)
            {
                ActiveChatGrid.Visibility = Visibility.Collapsed;
                EmptyStateGrid.Visibility = Visibility.Visible;
                return;
            }

            ActiveChatGrid.Visibility = Visibility.Visible;
            EmptyStateGrid.Visibility = Visibility.Collapsed;
            ChatTitleText.Text = chat.Name;

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
            
            foreach (var msg in messages)
            {
                _messages.Add(msg);
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

            // After initial load, if the list is still short/at top, load more until we have a scrollable area
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, async () => {
                await Task.Delay(1000); // Wait for virtualization/layout
                if (_scrollViewer != null && _scrollViewer.VerticalOffset < 200 && !_hasReachedStart && !_isLoadingMore)
                {
                    Debug.WriteLine("[ChatDetailView] Auto-triggering more messages after initial load (top reached)");
                    await LoadMoreMessagesAsync();
                }
            });
        }

        private void ScrollToBottom()
        {
            if (_messages.Count > 0)
            {
                MessageListView.ScrollIntoView(_messages[_messages.Count - 1]);
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
            if (string.IsNullOrWhiteSpace(text) || _activeChat == null) return;

            // Clear input immediately for responsiveness
            MessageInput.Text = "";

            try
            {
                // Send via WhatsApp service
                var msg = await WhatsAppService.Instance.SendTextMessageAsync(_activeChat.JID, text);
                
                // Add to local UI
                _messages.Add(msg);
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
                            await WhatsAppService.Instance.Socket.SendImageMessageAsync(_activeChat.JID, fileBytes);
                            
                            // Optimistic update (optional, usually handled by history sync echo or manual add)
                            // But for now, we rely on the sync or refresh.
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ChatView] Attach/Send Error: {ex}");
            }
        }
    }
}
