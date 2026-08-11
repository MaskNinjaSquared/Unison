using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Factories;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.ViewModels
{
    public class ChatListViewModel : Observable
    {
        private readonly IWhatsAppService _whatsAppService;
        private readonly IDispatcher _dispatcher;
        private readonly IChatItemVmFactory _factory;
        private readonly IStringResources _strings;
        private readonly IStatusBarService _statusBar;

        private CancellationTokenSource _refreshCts;

        private string _searchQuery;
        private string _syncStatusText;
        private bool _isSyncStatusVisible;
        private bool _isLoadingOverlayVisible;
        private bool _isRefreshing;
        private string _lastSelectedChatJid;
        private ChatItemViewModel _selectedChat;
        private bool _attached;

        public ChatListViewModel(
            IWhatsAppService whatsAppService,
            IDispatcher dispatcher,
            IChatItemVmFactory factory,
            IStringResources strings,
            IShellThemeService theme,
            IStatusBarService statusBar)
        {
            _whatsAppService = whatsAppService;
            _dispatcher = dispatcher;
            _factory = factory;
            _strings = strings;
            _statusBar = statusBar;

            // Strategy-driven: WhatsApp always inline; Unison Mobile uses StatusBar.
            DisplaySync = theme == null || theme.DisplaySyncInChatList;

            VisibleChats = new ObservableCollection<ChatItemViewModel>();

            RefreshContactNamesCommand = new RelayCommand(async () =>
            {
                await PresentSyncStatusAsync(_strings.Get("ChatList_RefreshingNames"), visible: true);
                try
                {
                    await _whatsAppService.RefreshContactNamesAsync(includeGroups: false, force: true);
                }
                finally
                {
                    RefreshVisibleChats();
                }
            });
        }

        /// <summary>
        /// Starts mirroring <see cref="IWhatsAppService.Chats"/> into <see cref="VisibleChats"/>.
        /// Call only when this VM owns the list UI (not while ChatListView code-behind still renders).
        /// </summary>
        public void Attach()
        {
            if (_attached) return;
            _attached = true;
            SubscribeToServiceEvents();
            Initialize();
        }

        public ObservableCollection<ChatItemViewModel> VisibleChats { get; }

        /// <summary>
        /// When true, sync/connecting text is shown in the chat-list header.
        /// Sourced from <see cref="IShellThemeService.DisplaySyncInChatList"/>.
        /// </summary>
        public bool DisplaySync { get; }

        /// <summary>UI panel should bind this (DisplaySync &amp;&amp; IsSyncStatusVisible).</summary>
        public bool ShowSyncStatusInUi => DisplaySync && IsSyncStatusVisible;

        public ChatItemViewModel SelectedChat
        {
            get => _selectedChat;
            set => Set(ref _selectedChat, value);
        }

        public bool IsRefreshing
        {
            get => _isRefreshing;
            private set => Set(ref _isRefreshing, value);
        }

        public string SearchQuery
        {
            get => _searchQuery;
            set
            {
                if (Set(ref _searchQuery, value))
                    RefreshVisibleChats();
            }
        }

        public string SyncStatusText
        {
            get => _syncStatusText;
            set => Set(ref _syncStatusText, value);
        }

        public bool IsSyncStatusVisible
        {
            get => _isSyncStatusVisible;
            set
            {
                if (Set(ref _isSyncStatusVisible, value))
                    OnPropertyChanged(nameof(ShowSyncStatusInUi));
            }
        }

        public bool IsLoadingOverlayVisible
        {
            get => _isLoadingOverlayVisible;
            set => Set(ref _isLoadingOverlayVisible, value);
        }

        public ICommand RefreshContactNamesCommand { get; }

        /// <summary>
        /// Shared presentation: header UI when <see cref="DisplaySync"/>;
        /// StatusBar progress only when the Unison Mobile strategy owns it.
        /// </summary>
        public async Task PresentSyncStatusAsync(string message, bool visible)
        {
            SyncStatusText = message ?? string.Empty;

            if (DisplaySync)
            {
                IsSyncStatusVisible = visible && !string.IsNullOrEmpty(message);
                if (_statusBar != null)
                {
                    await _statusBar.HideProgressAsync();
                }
                return;
            }

            IsSyncStatusVisible = false;
            if (_statusBar == null)
            {
                return;
            }

            if (visible && !string.IsNullOrEmpty(message))
            {
                await _statusBar.ShowProgressAsync(message);
            }
            else
            {
                await _statusBar.HideProgressAsync();
            }
        }

        private void Initialize()
        {
            IsLoadingOverlayVisible = _whatsAppService.Chats.Count == 0;

            foreach (var chat in _whatsAppService.Chats)
                chat.PropertyChanged += ChatItem_PropertyChanged;

            _ = PresentFromConnectionStatusAsync(_whatsAppService.CurrentConnectionStatus);
            RefreshVisibleChats();
        }

        private void SubscribeToServiceEvents()
        {
            _whatsAppService.OnConnectionUpdate += async (s, status) =>
                await _dispatcher.RunAsync(() => { _ = PresentFromConnectionStatusAsync(status); });

            _whatsAppService.OnSyncStatus += async (s, message) =>
                await _dispatcher.RunAsync(() =>
                {
                    if (!string.IsNullOrEmpty(message))
                        _ = PresentSyncStatusAsync(message, visible: true);
                    else
                        _ = PresentSyncStatusAsync(null, visible: false);
                });

            _whatsAppService.OnDisplayNamesUpdated += async (s, e) =>
                await _dispatcher.RunAsync(ScheduleRefreshVisibleChats);

            _whatsAppService.Chats.CollectionChanged += async (s, args) =>
                await _dispatcher.RunAsync(() =>
                {
                    if (_whatsAppService.Chats.Count > 0)
                        IsLoadingOverlayVisible = false;

                    if (args?.OldItems != null)
                        foreach (ChatItem old in args.OldItems)
                            old.PropertyChanged -= ChatItem_PropertyChanged;

                    if (args?.NewItems != null)
                        foreach (ChatItem newItem in args.NewItems)
                            newItem.PropertyChanged += ChatItem_PropertyChanged;

                    ScheduleRefreshVisibleChats();
                });
        }

        private async Task PresentFromConnectionStatusAsync(string status)
        {
            switch (status)
            {
                case "connecting":
                    await PresentSyncStatusAsync(_strings.Get("ChatList_Connecting"), visible: true);
                    break;
                case "connected":
                    await PresentSyncStatusAsync(_strings.Get("ChatList_Handshake"), visible: true);
                    break;
                case "open":
                    await PresentSyncStatusAsync(_strings.Get("ChatList_Updating"), visible: true);
                    break;
                case "close":
                case "synced":
                    await PresentSyncStatusAsync(null, visible: false);
                    break;
                default:
                    if (!string.IsNullOrEmpty(status))
                        await PresentSyncStatusAsync(status, visible: true);
                    break;
            }
        }

        private void ChatItem_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            var chat = sender as ChatItem;
            if (chat == null) return;

            if (e.PropertyName == nameof(ChatItem.Name) ||
                e.PropertyName == nameof(ChatItem.LastMessage) ||
                e.PropertyName == nameof(ChatItem.Timestamp) ||
                e.PropertyName == nameof(ChatItem.AvatarUrl) ||
                e.PropertyName == nameof(ChatItem.UnreadCount) ||
                e.PropertyName == nameof(ChatItem.IsPinned))
            {
                if (!string.IsNullOrWhiteSpace(SearchQuery) ||
                    e.PropertyName == nameof(ChatItem.Timestamp) ||
                    e.PropertyName == nameof(ChatItem.IsPinned))
                {
                    _ = _dispatcher.RunAsync(ScheduleRefreshVisibleChats);
                }
            }
        }

        public void RefreshVisibleChats()
        {
            string query = SearchQuery?.Trim() ?? string.Empty;
            var source = _whatsAppService.Chats.ToList();

            if (!string.IsNullOrEmpty(query))
            {
                source = source.Where(c =>
                    (!string.IsNullOrEmpty(c.Name) && c.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (_whatsAppService.ResolveDisplayName(c.JID, "search").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (!string.IsNullOrEmpty(c.LastMessage) && c.LastMessage.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (!string.IsNullOrEmpty(c.JID) && c.JID.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            }

            source = DeduplicateByCanonicalJid(source);

            IsRefreshing = true;
            try
            {
                VisibleChats.Clear();
                foreach (var item in source)
                    VisibleChats.Add(_factory.Create(item));

                if (!string.IsNullOrWhiteSpace(_lastSelectedChatJid))
                {
                    string canonical = _whatsAppService.GetCanonicalJid(_lastSelectedChatJid);
                    var restored = VisibleChats.FirstOrDefault(vm =>
                        string.Equals(vm.JID, _lastSelectedChatJid, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(_whatsAppService.GetCanonicalJid(vm.JID), canonical, StringComparison.OrdinalIgnoreCase));
                    SelectedChat = restored;
                    if (restored != null)
                        _lastSelectedChatJid = restored.JID;
                }
            }
            finally
            {
                IsRefreshing = false;
            }
        }

        public void OnChatSelected(ChatItemViewModel vm)
        {
            _lastSelectedChatJid = vm?.JID;
            SelectedChat = vm;
        }

        private void ScheduleRefreshVisibleChats()
        {
            _refreshCts?.Cancel();
            _refreshCts = new CancellationTokenSource();
            var token = _refreshCts.Token;

            Task.Delay(80, token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                _ = _dispatcher.RunAsync(RefreshVisibleChats);
            });
        }

        private List<ChatItem> DeduplicateByCanonicalJid(List<ChatItem> source)
        {
            var deduped = new List<ChatItem>();
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in source)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.JID)) continue;
                string canonical = _whatsAppService.GetCanonicalJid(item.JID);
                if (string.IsNullOrWhiteSpace(canonical)) canonical = item.JID;

                if (!seen.TryGetValue(canonical, out var existingIdx))
                {
                    seen[canonical] = deduped.Count;
                    deduped.Add(item);
                }
                else
                {
                    var existing = deduped[existingIdx];
                    if (string.IsNullOrWhiteSpace(existing.AvatarUrl) && !string.IsNullOrWhiteSpace(item.AvatarUrl))
                        deduped[existingIdx] = item;
                }
            }
            return deduped;
        }
    }
}
