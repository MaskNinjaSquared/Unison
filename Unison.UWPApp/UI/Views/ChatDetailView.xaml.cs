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

namespace Unison.UWPApp.UI.Views
{
    public sealed partial class ChatDetailView : UserControl
    {
        private ChatItem _activeChat;
        private ObservableCollection<ChatMessage> _messages = new ObservableCollection<ChatMessage>();
        public event EventHandler BackRequested;

        public ChatDetailView()
        {
            this.InitializeComponent();
            MessageListView.ItemsSource = _messages;
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            BackRequested?.Invoke(this, EventArgs.Empty);
        }

        public void SetActiveChat(ChatItem chat)
        {
            _activeChat = chat;
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

            // Load messages from service
            _messages.Clear();
            if (WhatsAppService.Instance.MessagesByChat.ContainsKey(chat.JID))
            {
                foreach (var msg in WhatsAppService.Instance.MessagesByChat[chat.JID])
                {
                    _messages.Add(msg);
                }
            }

            ScrollToBottom();
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
