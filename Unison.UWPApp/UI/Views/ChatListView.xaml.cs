using System;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Unison.UWPApp.Models;
using Unison.UWPApp.Services;
using Unison.UWPApp.Protocol;
using Proto;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.UWPApp.UI.Views
{
    public class ChatSelectedEventArgs : EventArgs
    {
        public ChatItem SelectedChat { get; }
        public ChatSelectedEventArgs(ChatItem chat) => SelectedChat = chat;
    }

    public sealed partial class ChatListView : UserControl
    {
        public ObservableCollection<ChatItem> Chats => WhatsAppService.Instance.Chats;
        public event EventHandler<ChatSelectedEventArgs> ChatSelected;
        public event EventHandler MenuClicked;

        public ChatListView()
        {
            this.InitializeComponent();
            this.Loaded += ChatListView_Loaded;
        }

        private void ChatListView_Loaded(object sender, RoutedEventArgs e)
        {
            WhatsAppService.Instance.OnConnectionUpdate += (s, status) => 
            {
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => UpdateSyncStatus(status));
            };

            WhatsAppService.Instance.OnHistorySyncReceived += (s, sync) => 
            {
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => 
                {
                    SyncStatusBar.Visibility = Visibility.Collapsed;
                    ChatLoadingOverlay.Visibility = Visibility.Collapsed;
                });
            };

            // Subscribe to collection changes to hide overlay when chats are added
            WhatsAppService.Instance.Chats.CollectionChanged += (s, args) =>
            {
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    if (WhatsAppService.Instance.Chats.Count > 0)
                    {
                        ChatLoadingOverlay.Visibility = Visibility.Collapsed;
                    }
                });
            };

            // Subscribe to sync status updates (e.g., "Fetching contact names...", "Fetching profile pictures...")
            WhatsAppService.Instance.OnSyncStatus += (s, statusMessage) =>
            {
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    if (!string.IsNullOrEmpty(statusMessage))
                    {
                        SyncStatusBar.Visibility = Visibility.Visible;
                        SyncStatusText.Text = statusMessage;
                    }
                    else
                    {
                        SyncStatusBar.Visibility = Visibility.Collapsed;
                    }
                });
            };

            // Initial state - hide overlay if chats already loaded
            if (WhatsAppService.Instance.Chats.Count > 0)
            {
                ChatLoadingOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateSyncStatus(string status)
        {
            switch (status)
            {
                case "connecting":
                    SyncStatusBar.Visibility = Visibility.Visible;
                    SyncStatusText.Text = "Connecting...";
                    break;
                case "connected":
                    SyncStatusBar.Visibility = Visibility.Visible;
                    SyncStatusText.Text = "Handshake...";
                    break;
                case "open":
                    SyncStatusBar.Visibility = Visibility.Visible;
                    SyncStatusText.Text = "Updating...";
                    break;
                case "close":
                    SyncStatusBar.Visibility = Visibility.Collapsed;
                    break;
            }
        }

        private void ChatList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ChatList.SelectedItem is ChatItem chat)
            {
                ChatSelected?.Invoke(this, new ChatSelectedEventArgs(chat));
            }
        }

        public void ClearSelection()
        {
            ChatList.SelectedItem = null;
        }

        private async void NewChatButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new NewChatDialog();
            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary && !string.IsNullOrEmpty(dialog.ResolvedJid))
            {
                // Start a new chat session
                var chat = Chats.FirstOrDefault(c => c.JID == dialog.ResolvedJid);
                if (chat == null)
                {
                    WhatsAppService.Instance.StartNewChat(dialog.ResolvedJid);
                    // The chat will be added to the collection via dispatcher
                    // For immediate selection, we can wait a bit or use a more robust way
                    await Task.Delay(100); 
                    chat = Chats.FirstOrDefault(c => c.JID == dialog.ResolvedJid);
                }

                if (chat != null)
                {
                    ChatList.SelectedItem = chat;
                    ChatSelected?.Invoke(this, new ChatSelectedEventArgs(chat));
                }
            }
        }

        private void MenuButton_Click(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("[ChatListView] MenuButton_Click");
            MenuClicked?.Invoke(this, EventArgs.Empty);
        }
    }
}
