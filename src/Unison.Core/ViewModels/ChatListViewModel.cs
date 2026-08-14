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
        private readonly IMessageService _messageService;
        private readonly IContactService _contactService;
        private readonly IShortcutService _shortcutService;
        private readonly IChatStore _chatStore;
        private readonly IDispatcher _dispatcher;
        private readonly IChatItemVmFactory _factory;
        private readonly IStringResources _strings;
        private readonly IStatusBarService _statusBar;
        private readonly IDialogService _dialogService;

        private CancellationTokenSource _refreshCts;

        private string _searchQuery;
        private string _syncStatusText;
        private bool _isSyncStatusVisible;
        private bool _isLoadingOverlayVisible;
        private bool _isRefreshing;
        private string _lastSelectedChatJid;
        private ChatItemViewModel _selectedChat;
        private bool _attached;
        private bool _menuActionBusy;
        private readonly RelayCommand _refreshContactNamesCommand;
        private readonly RelayCommand _resyncConversationsCommand;

        /// <summary>
        /// Builds a fresh <see cref="NewChatDialogViewModel"/> per New Chat dialog
        /// (clean phone/error state; same pattern as <see cref="IChatItemVmFactory"/>).
        /// </summary>
        private readonly INewChatDialogViewModelFactory _newChatFactory;

        public ChatListViewModel(
            IWhatsAppService whatsAppService,
            IMessageService messageService,
            IContactService contactService,
            IShortcutService shortcutService,
            IChatStore chatStore,
            IDispatcher dispatcher,
            IChatItemVmFactory factory,
            IStringResources strings,
            IShellThemeService theme,
            IStatusBarService statusBar,
            IDialogService dialogService,
            INewChatDialogViewModelFactory newChatFactory)
        {
            _whatsAppService = whatsAppService;
            _messageService = messageService ?? throw new ArgumentNullException(nameof(messageService));
            _contactService = contactService ?? throw new ArgumentNullException(nameof(contactService));
            _shortcutService = shortcutService ?? throw new ArgumentNullException(nameof(shortcutService));
            _chatStore = chatStore;
            _dispatcher = dispatcher;
            _factory = factory;
            _strings = strings;
            _statusBar = statusBar;
            _dialogService = dialogService;
            _newChatFactory = newChatFactory ?? throw new ArgumentNullException(nameof(newChatFactory));

            // Strategy-driven: WhatsApp always inline; Unison Mobile uses StatusBar.
            DisplaySync = theme == null || theme.DisplaySyncInChatList;

            VisibleChats = new ObservableCollection<ChatItemViewModel>();

            _refreshContactNamesCommand = new RelayCommand(
                () => _ = RefreshContactNamesAsync(),
                CanExecuteMenuAction);
            _resyncConversationsCommand = new RelayCommand(
                () => _ = ResyncConversationsAsync(),
                CanExecuteMenuAction);

            RefreshContactNamesCommand = _refreshContactNamesCommand;
            ResyncConversationsCommand = _resyncConversationsCommand;
            PinChatToStartCommand = new RelayCommand<ChatItem>(
                chat => _ = ToggleWidgetPinAsync(chat),
                chat => chat != null && !string.IsNullOrWhiteSpace(chat.JID));
            SetLocalMuteCommand = new RelayCommand<ChatMuteRequest>(
                request => _ = SetLocalMuteAsync(request),
                request => request?.Chat != null &&
                           !string.IsNullOrWhiteSpace(request.Chat.JID) &&
                           _chatStore != null);
            OpenMenuCommand = new RelayCommand(() => MenuRequested?.Invoke(this, EventArgs.Empty));
            NewChatCommand = new RelayCommand(() => _ = StartNewChatAsync());

            // Keep CanExecute in sync even when Attach() is not used (hybrid list UI).
            _whatsAppService.OnInitialSyncProgress += (s, e) =>
                _ = _dispatcher.RunAsync(RaiseMenuCommandsCanExecuteChanged);
        }

        /// <summary>
        /// Raised just before local chats are wiped (code-behind clears selection / visible list).
        /// </summary>
        public event EventHandler BeforeLocalConversationsCleared;

        /// <summary>
        /// Raised after a menu action that mutated chats (code-behind refreshes its ItemsSource).
        /// </summary>
        public event EventHandler AfterMenuActionCompleted;

        /// <summary>Header "…" menu — shell listens via ChatListView.MenuClicked.</summary>
        public event EventHandler MenuRequested;

        /// <summary>New-chat flow resolved a JID; hybrid list should select that chat.</summary>
        public event EventHandler<string> OpenChatRequested;

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
                // Hybrid ChatListView still owns filtering; only refresh VM list when Attach()d.
                if (Set(ref _searchQuery, value) && _attached)
                {
                    RefreshVisibleChats();
                }
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
                {
                    RaiseSyncStatusUiChanged();
                }
            }
        }

        public bool IsLoadingOverlayVisible
        {
            get => _isLoadingOverlayVisible;
            set => Set(ref _isLoadingOverlayVisible, value);
        }

        /// <summary>Re-queries contact / LID display names from the server for the chat list.</summary>
        public ICommand RefreshContactNamesCommand { get; }

        /// <summary>Wipes local chats/messages (keeps auth) and re-pulls conversation history.</summary>
        public ICommand ResyncConversationsCommand { get; }

        /// <summary>Pins/unpins the chat Start live tile (<see cref="ChatItem.IsWidgetPinned"/>).</summary>
        public ICommand PinChatToStartCommand { get; }

        /// <summary>Sets or clears local/unified <see cref="ChatItem.MutedUntil"/>.</summary>
        public ICommand SetLocalMuteCommand { get; }

        /// <summary>Raises <see cref="MenuRequested"/> so the shell opens settings / overflow.</summary>
        public ICommand OpenMenuCommand { get; }

        /// <summary>Opens the new-chat dialog, creates the chat if needed, then raises <see cref="OpenChatRequested"/>.</summary>
        public ICommand NewChatCommand { get; }

        private async Task StartNewChatAsync()
        {
            string jid = await _dialogService.ShowNewChatDialogAsync(_newChatFactory.Create());
            if (string.IsNullOrEmpty(jid))
            {
                return;
            }

            bool exists = false;
            foreach (ChatItem chat in _whatsAppService.Chats)
            {
                if (chat != null && string.Equals(chat.JID, jid, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }

            if (!exists)
            {
                _messageService.StartNewChat(jid);
                await Task.Delay(100);
            }

            OpenChatRequested?.Invoke(this, jid);
        }

        private async Task ToggleWidgetPinAsync(ChatItem chat)
        {
            if (chat == null || string.IsNullOrWhiteSpace(chat.JID) || _shortcutService == null || _chatStore == null)
            {
                return;
            }

            try
            {
                _chatStore.ApplyTo(chat);
                bool nextPinned = !chat.IsWidgetPinned;
                bool ok = nextPinned
                    ? await _shortcutService.PinChatAsync(chat)
                    : await _shortcutService.UnpinChatAsync(chat.JID);

                if (!ok && nextPinned)
                {
                    return;
                }

                chat.IsWidgetPinned = nextPinned;
                await _chatStore.UpsertAsync(
                    chat.JID,
                    chat.LocalStatus,
                    chat.IsWidgetPinned,
                    chat.IsChatPinned,
                    chat.MutedUntil);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatListViewModel] ToggleWidgetPin failed: " + ex.Message);
            }
        }

        private async Task SetLocalMuteAsync(ChatMuteRequest request)
        {
            if (request?.Chat == null || string.IsNullOrWhiteSpace(request.Chat.JID) || _chatStore == null)
            {
                return;
            }

            ChatItem chat = request.Chat;
            try
            {
                _chatStore.ApplyTo(chat);
                chat.MutedUntil = request.MutedUntil;
                await _chatStore.UpsertAsync(
                    chat.JID,
                    chat.LocalStatus,
                    chat.IsWidgetPinned,
                    chat.IsChatPinned,
                    chat.MutedUntil);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatListViewModel] SetLocalMute failed: " + ex.Message);
            }
        }

        /// <summary>True while user resync owns the sync status ring (ignore transient OnSyncStatus clears).</summary>
        public bool IsConversationResyncInProgress => _awaitingResyncHistory;

        private bool CanExecuteMenuAction()
        {
            return !_menuActionBusy &&
                   _whatsAppService != null &&
                   !_whatsAppService.IsInitialSyncSafeMode;
        }

        private void RaiseMenuCommandsCanExecuteChanged()
        {
            _refreshContactNamesCommand?.RaiseCanExecuteChanged();
            _resyncConversationsCommand?.RaiseCanExecuteChanged();
        }

        private void RaiseSyncStatusUiChanged() =>
            OnPropertyChanged(nameof(ShowSyncStatusInUi));

        private async Task RefreshContactNamesAsync()
        {
            if (!CanExecuteMenuAction())
            {
                return;
            }

            _menuActionBusy = true;
            RaiseMenuCommandsCanExecuteChanged();
            await PresentSyncStatusAsync(
                _strings.Get("ChatList_RefreshingNames", "Refreshing contact names..."),
                visible: true);
            try
            {
                await _contactService.RefreshContactNamesAsync(includeGroups: false, force: true);
            }
            finally
            {
                RefreshVisibleChats();
                AfterMenuActionCompleted?.Invoke(this, EventArgs.Empty);
                await PresentSyncStatusAsync(null, visible: false);
                _menuActionBusy = false;
                RaiseMenuCommandsCanExecuteChanged();
            }
        }

        private bool _awaitingResyncHistory;

        private async Task ResyncConversationsAsync()
        {
            if (!CanExecuteMenuAction())
            {
                return;
            }

            bool confirmed = await _dialogService.ShowConfirmAsync(
                title: _strings.Get("ChatList_ResyncConversationsTitle", "Re-sync conversations?"),
                content: _strings.Get(
                    "ChatList_ResyncConversationsBody",
                    "This deletes all local chats and messages, then downloads history again. Your WhatsApp link stays active."),
                primaryButtonText: _strings.Get("ChatList_ResyncConversationsConfirm", "Re-sync"),
                closeButtonText: _strings.Get("ChatList_ResyncConversationsCancel", "Cancel"));

            if (!confirmed)
            {
                return;
            }

            _menuActionBusy = true;
            RaiseMenuCommandsCanExecuteChanged();
            _awaitingResyncHistory = true;
            try
            {
                BeforeLocalConversationsCleared?.Invoke(this, EventArgs.Empty);

                var progress = new Progress<ConversationResyncPhase>(phase =>
                {
                    _ = _dispatcher.RunAsync(() =>
                    {
                        switch (phase)
                        {
                            case ConversationResyncPhase.CleaningHistory:
                                _ = PresentSyncStatusAsync(
                                    _strings.Get("ChatList_ResyncCleaningHistory", "Cleaning history..."),
                                    visible: true);
                                break;
                            case ConversationResyncPhase.PreparingConversations:
                                _ = PresentSyncStatusAsync(
                                    _strings.Get("ChatList_ResyncingConversations", "Re-syncing conversations..."),
                                    visible: true);
                                IsLoadingOverlayVisible = true;
                                RefreshVisibleChats();
                                AfterMenuActionCompleted?.Invoke(this, EventArgs.Empty);
                                break;
                        }
                    });
                });

                await PresentSyncStatusAsync(
                    _strings.Get("ChatList_ResyncCleaningHistory", "Cleaning history..."),
                    visible: true);

                await _messageService.ResyncConversationsAsync(progress);
            }
            finally
            {
                _awaitingResyncHistory = false;
                RefreshVisibleChats();
                AfterMenuActionCompleted?.Invoke(this, EventArgs.Empty);
                await PresentSyncStatusAsync(null, visible: false);
                IsLoadingOverlayVisible = false;
                _menuActionBusy = false;
                RaiseMenuCommandsCanExecuteChanged();
            }
        }

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
                await _dispatcher.RunAsync(() =>
                {
                    if (_awaitingResyncHistory)
                    {
                        // Still show reconnecting / syncing states while waiting for history.
                        if (string.Equals(status, "reconnecting", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(status, "connecting", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(status, "open", StringComparison.OrdinalIgnoreCase))
                        {
                            _ = PresentFromConnectionStatusAsync(status);
                        }

                        return;
                    }

                    _ = PresentFromConnectionStatusAsync(status);
                });

            _whatsAppService.OnSyncStatus += async (s, message) =>
                await _dispatcher.RunAsync(() =>
                {
                    if (_awaitingResyncHistory)
                    {
                        // Surface service progress ("Re-syncing...", "Preparing...") instead of
                        // freezing on the wipe banner until the wait completes.
                        if (!string.IsNullOrEmpty(message) &&
                            !string.Equals(message, "Saving chats...", StringComparison.Ordinal))
                        {
                            _ = PresentSyncStatusAsync(message, visible: true);
                        }

                        return;
                    }

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
            if (chat == null)
            {
                return;
            }

            if (!ShouldRefreshVisibleChats(e.PropertyName))
            {
                return;
            }

            _ = _dispatcher.RunAsync(ScheduleRefreshVisibleChats);
        }

        /// <summary>
        /// Name/preview tweaks only re-filter while searching; pin/time always re-sort the list.
        /// </summary>
        private bool ShouldRefreshVisibleChats(string propertyName)
        {
            if (propertyName == nameof(ChatItem.Timestamp) ||
                propertyName == nameof(ChatItem.IsChatPinned))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(SearchQuery))
            {
                return false;
            }

            return propertyName == nameof(ChatItem.Name) ||
                   propertyName == nameof(ChatItem.LastMessage) ||
                   propertyName == nameof(ChatItem.AvatarUrl) ||
                   propertyName == nameof(ChatItem.UnreadCount);
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
