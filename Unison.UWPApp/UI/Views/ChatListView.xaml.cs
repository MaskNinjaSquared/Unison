using System;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.ComponentModel;
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
        public ObservableCollection<ChatItem> VisibleChats { get; } = new ObservableCollection<ChatItem>();
        public event EventHandler<ChatSelectedEventArgs> ChatSelected;
        public event EventHandler MenuClicked;
        private bool _subscriptionsAttached;
        private bool _isRefreshingVisibleChats;
        private string _lastSelectedChatJid;

        public ChatListView()
        {
            this.InitializeComponent();
            this.Loaded += ChatListView_Loaded;
        }

        private void ChatListView_Loaded(object sender, RoutedEventArgs e)
        {
            if (_subscriptionsAttached)
            {
                return;
            }
            _subscriptionsAttached = true;

            WhatsAppService.Instance.OnConnectionUpdate += (s, status) => 
            {
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => UpdateSyncStatus(status));
            };

            WhatsAppService.Instance.OnHistorySyncReceived += (s, sync) => 
            {
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => 
                {
                    SyncStatusPanel.Visibility = Visibility.Collapsed;
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

                    if (args != null)
                    {
                        if (args.OldItems != null)
                        {
                            foreach (ChatItem oldItem in args.OldItems)
                            {
                                if (oldItem != null) oldItem.PropertyChanged -= ChatItem_PropertyChanged;
                            }
                        }
                        if (args.NewItems != null)
                        {
                            foreach (ChatItem newItem in args.NewItems)
                            {
                                if (newItem != null) newItem.PropertyChanged += ChatItem_PropertyChanged;
                            }
                        }
                    }

                    if (!TryApplyIncrementalCollectionChange(args))
                    {
                        RefreshVisibleChats();
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
                        SyncStatusPanel.Visibility = Visibility.Visible;
                        SyncStatusText.Text = statusMessage;
                    }
                    else
                    {
                        SyncStatusPanel.Visibility = Visibility.Collapsed;
                    }
                });
            };

            WhatsAppService.Instance.OnDisplayNamesUpdated += (s, ev) =>
            {
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, RefreshVisibleChats);
            };

            // Initial state - hide overlay if chats already loaded
            if (WhatsAppService.Instance.Chats.Count > 0)
            {
                ChatLoadingOverlay.Visibility = Visibility.Collapsed;
            }

            foreach (var chat in WhatsAppService.Instance.Chats)
            {
                chat.PropertyChanged += ChatItem_PropertyChanged;
            }
            UpdateSyncStatus(WhatsAppService.Instance.CurrentConnectionStatus);
            RefreshVisibleChats();
        }

        private void UpdateSyncStatus(string status)
        {
            switch (status)
            {
                case "connecting":
                    SyncStatusPanel.Visibility = Visibility.Visible;
                    SyncStatusText.Text = "Connecting...";
                    break;
                case "connected":
                    SyncStatusPanel.Visibility = Visibility.Visible;
                    SyncStatusText.Text = "Handshake...";
                    break;
                case "open":
                    SyncStatusPanel.Visibility = Visibility.Visible;
                    SyncStatusText.Text = "Updating...";
                    break;
                case "close":
                case "synced":
                    SyncStatusPanel.Visibility = Visibility.Collapsed;
                    break;
            }
        }

        private void ChatList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var chat = ChatList.SelectedItem as ChatItem;
            if (_isRefreshingVisibleChats && chat == null)
            {
                return;
            }

            if (chat != null)
            {
                _lastSelectedChatJid = chat.JID;
            }
            else
            {
                _lastSelectedChatJid = null;
            }
            ChatSelected?.Invoke(this, new ChatSelectedEventArgs(chat));
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

        private async void RefreshContactNamesMenuItem_Click(object sender, RoutedEventArgs e)
        {
            SyncStatusPanel.Visibility = Visibility.Visible;
            SyncStatusText.Text = "Refreshing contact names...";
            try
            {
                await WhatsAppService.Instance.RefreshContactNamesAsync(includeGroups: false, force: true);
            }
            finally
            {
                RefreshVisibleChats();
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            RefreshVisibleChats();
        }

        private void AvatarImage_ImageOpened(object sender, RoutedEventArgs e)
        {
            var image = sender as Image;
            var host = image?.Parent as FrameworkElement;
            if (host != null)
            {
                host.Visibility = Visibility.Visible;
            }
        }

        private void AvatarImage_ImageFailed(object sender, ExceptionRoutedEventArgs e)
        {
            var image = sender as Image;
            var host = image?.Parent as FrameworkElement;
            if (host != null)
            {
                host.Visibility = Visibility.Collapsed;
            }

            var chat = image?.DataContext as ChatItem;
            if (chat != null)
            {
                WhatsAppService.Instance.MarkAvatarImageLoadFailed(chat, "ui-image-failed:" + (e?.ErrorMessage ?? "unknown"));
            }
        }

        private void ChatItem_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            var chat = sender as ChatItem;
            if (chat == null)
            {
                return;
            }

            if (e.PropertyName == nameof(ChatItem.LastMessage) ||
                e.PropertyName == nameof(ChatItem.Timestamp))
            {
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                {
                    if (!TryApplyIncrementalPropertyChange(chat, e.PropertyName))
                    {
                        RefreshVisibleChats();
                    }
                });
                return;
            }

            if (e.PropertyName == nameof(ChatItem.Name) ||
                e.PropertyName == nameof(ChatItem.AvatarUrl))
            {
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                {
                    if (HasActiveSearchQuery() || !CanUseIncrementalVisibleChatUpdates())
                    {
                        RefreshVisibleChats();
                    }
                });
            }
        }

        private bool TryApplyIncrementalCollectionChange(NotifyCollectionChangedEventArgs args)
        {
            if (args == null || !CanUseIncrementalVisibleChatUpdates())
            {
                return false;
            }

            switch (args.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    if (args.NewItems == null)
                    {
                        return false;
                    }

                    foreach (ChatItem newItem in args.NewItems)
                    {
                        if (newItem == null || VisibleChats.Contains(newItem))
                        {
                            continue;
                        }

                        int targetIndex = Chats.IndexOf(newItem);
                        if (targetIndex < 0)
                        {
                            return false;
                        }

                        VisibleChats.Insert(Math.Min(targetIndex, VisibleChats.Count), newItem);
                    }
                    return true;

                case NotifyCollectionChangedAction.Remove:
                    if (args.OldItems == null)
                    {
                        return false;
                    }

                    foreach (ChatItem oldItem in args.OldItems)
                    {
                        if (oldItem == null)
                        {
                            continue;
                        }

                        int existingIndex = VisibleChats.IndexOf(oldItem);
                        if (existingIndex >= 0)
                        {
                            VisibleChats.RemoveAt(existingIndex);
                        }
                    }
                    return true;

                case NotifyCollectionChangedAction.Move:
                    if (args.NewItems == null || args.NewItems.Count != 1)
                    {
                        return false;
                    }

                    var movedItem = args.NewItems[0] as ChatItem;
                    if (movedItem == null)
                    {
                        return false;
                    }

                    return MoveVisibleChatToMatchSource(movedItem);

                case NotifyCollectionChangedAction.Replace:
                    if (args.OldItems == null || args.NewItems == null)
                    {
                        return false;
                    }

                    foreach (ChatItem oldItem in args.OldItems)
                    {
                        if (oldItem == null)
                        {
                            continue;
                        }

                        int existingIndex = VisibleChats.IndexOf(oldItem);
                        if (existingIndex >= 0)
                        {
                            VisibleChats.RemoveAt(existingIndex);
                        }
                    }

                    foreach (ChatItem newItem in args.NewItems)
                    {
                        if (newItem == null || VisibleChats.Contains(newItem))
                        {
                            continue;
                        }

                        int targetIndex = Chats.IndexOf(newItem);
                        if (targetIndex < 0)
                        {
                            return false;
                        }

                        VisibleChats.Insert(Math.Min(targetIndex, VisibleChats.Count), newItem);
                    }
                    return true;

                default:
                    return false;
            }
        }

        private bool TryApplyIncrementalPropertyChange(ChatItem chat, string propertyName)
        {
            if (chat == null || !CanUseIncrementalVisibleChatUpdates())
            {
                return false;
            }

            if (!VisibleChats.Contains(chat))
            {
                return false;
            }

            if (propertyName == nameof(ChatItem.Timestamp))
            {
                return MoveVisibleChatToMatchSource(chat);
            }

            return true;
        }

        private bool MoveVisibleChatToMatchSource(ChatItem chat)
        {
            if (chat == null)
            {
                return false;
            }

            int sourceIndex = Chats.IndexOf(chat);
            int visibleIndex = VisibleChats.IndexOf(chat);
            if (sourceIndex < 0 || visibleIndex < 0)
            {
                return false;
            }

            int targetIndex = Math.Min(sourceIndex, VisibleChats.Count - 1);
            if (targetIndex < 0)
            {
                return false;
            }

            if (visibleIndex != targetIndex)
            {
                VisibleChats.Move(visibleIndex, targetIndex);
            }

            return true;
        }

        private bool HasActiveSearchQuery()
        {
            return !string.IsNullOrWhiteSpace(SearchBox?.Text?.Trim());
        }

        private bool CanUseIncrementalVisibleChatUpdates()
        {
            if (HasActiveSearchQuery())
            {
                return false;
            }

            var service = WhatsAppService.Instance;
            var canonicalJids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var chat in Chats)
            {
                if (chat == null || string.IsNullOrWhiteSpace(chat.JID))
                {
                    continue;
                }

                string canonical = service.GetCanonicalJid(chat.JID);
                if (string.IsNullOrWhiteSpace(canonical))
                {
                    canonical = chat.JID;
                }

                if (!canonicalJids.Add(canonical))
                {
                    return false;
                }
            }

            return true;
        }

        private void RefreshVisibleChats()
        {
            var service = WhatsAppService.Instance;
            string selectedJid = (ChatList.SelectedItem as ChatItem)?.JID ?? _lastSelectedChatJid;
            string query = SearchBox?.Text?.Trim() ?? string.Empty;
            var source = Chats.ToList();
            if (!string.IsNullOrEmpty(query))
            {
                source = source.Where(c =>
                    (!string.IsNullOrEmpty(c.Name) && c.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (service.ResolveDisplayName(c.JID, "search").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (!string.IsNullOrEmpty(c.LastMessage) && c.LastMessage.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (!string.IsNullOrEmpty(c.JID) && c.JID.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            }

            // UI-level guard: if canonical duplicates still exist transiently, show only one row.
            var deduped = new List<ChatItem>();
            var canonicalIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in source)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.JID)) continue;
                string canonical = service.GetCanonicalJid(item.JID);
                if (string.IsNullOrWhiteSpace(canonical))
                {
                    canonical = item.JID;
                }

                if (!canonicalIndex.TryGetValue(canonical, out var existingIndex))
                {
                    canonicalIndex[canonical] = deduped.Count;
                    deduped.Add(item);
                    continue;
                }

                var existing = deduped[existingIndex];
                bool currentPreferred = string.IsNullOrWhiteSpace(existing.AvatarUrl) && !string.IsNullOrWhiteSpace(item.AvatarUrl);
                if (currentPreferred)
                {
                    deduped[existingIndex] = item;
                }
            }
            source = deduped;

            _isRefreshingVisibleChats = true;
            try
            {
                VisibleChats.Clear();
                foreach (var item in source)
                {
                    VisibleChats.Add(item);
                }

                if (!string.IsNullOrWhiteSpace(selectedJid))
                {
                    string selectedCanonical = service.GetCanonicalJid(selectedJid);
                    var selected = VisibleChats.FirstOrDefault(c =>
                        string.Equals(c.JID, selectedJid, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(service.GetCanonicalJid(c.JID), selectedCanonical, StringComparison.OrdinalIgnoreCase));
                    if (selected != null)
                    {
                        ChatList.SelectedItem = selected;
                        _lastSelectedChatJid = selected.JID;
                    }
                }
            }
            finally
            {
                _isRefreshingVisibleChats = false;
            }
        }

        public static Visibility GetContactFallbackVisibility(string avatarUrl, bool isGroup)
        {
            return !isGroup ? Visibility.Visible : Visibility.Collapsed;
        }

        public static Visibility GetGroupFallbackVisibility(string avatarUrl, bool isGroup)
        {
            return isGroup ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
