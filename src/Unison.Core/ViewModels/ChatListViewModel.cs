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
using Unison.Core.State;

namespace Unison.Core.ViewModels
{
    /// <summary>
    /// The chat list: which conversations are on screen, in what order, and what the header says
    /// while the app is busy.
    /// </summary>
    /// <remarks>
    /// This owns <see cref="VisibleChats"/> outright. The list the store gives us is not the list
    /// the user should see - it needs the search filter, the PN/LID dedupe and the pinned-first
    /// ordering applied, and none of that is layout, so none of it belongs in a view.
    /// <para>
    /// Rows are <see cref="ChatItem"/> and not a per-row view model. The model already raises
    /// change notifications, every consumer of this list - the detail pane, the shell, the
    /// shortcuts - speaks <see cref="ChatItem"/>, and a wrapper would buy nothing but a
    /// translation layer at every boundary.
    /// </para>
    /// <para>
    /// The collection is mutated in place rather than rebuilt. A clear-and-refill loses the scroll
    /// position and the selection on every incoming message, which during history sync is
    /// constant.
    /// </para>
    /// </remarks>
    public class ChatListViewModel : Observable
    {
        /// <summary>
        /// Still needed for identity questions the store cannot answer: which JIDs are the same
        /// person (PN vs LID) and what a chat is called when the row itself has no name.
        /// </summary>
        private readonly IWhatsAppService _whatsAppService;

        /// <summary>
        /// Where the chat list comes from. Read through the store rather than through the service,
        /// so this view model no longer depends on which class happens to be producing the data.
        /// </summary>
        private readonly IChatStateStore _chatState;

        private readonly IMessageService _messageService;
        private readonly IContactService _contactService;

        /// <summary>Transport state for the header line: connecting, open, reconnecting.</summary>
        private readonly IConnectionService _connection;

        /// <summary>Sync progress and the status sentence that goes with it.</summary>
        private readonly IHistoryService _history;

        private readonly IShortcutService _shortcutService;
        private readonly IChatStore _chatStore;
        private readonly IChatService _chatService;
        private readonly IDispatcher _dispatcher;
        private readonly IStringResources _strings;
        private readonly IStatusBarService _statusBar;
        private readonly IDialogService _dialogService;

        private CancellationTokenSource _refreshCts;

        private string _searchQuery;
        private string _syncStatusText;
        private bool _isSyncStatusVisible;
        private bool _isLoadingOverlayVisible;
        private string _loadingOverlayText;
        private bool _isRefreshing;
        private string _lastSelectedChatJid;
        private ChatItem _selectedChat;
        private bool _attached;
        private bool _menuActionBusy;
        private bool _awaitingResyncHistory;
        private readonly RelayCommand _refreshContactNamesCommand;
        private readonly RelayCommand _resyncConversationsCommand;

        // ---------------------------------------------------------------------
        // Batch rendering
        //
        // The first sync after login can produce thousands of conversations in a burst. Putting
        // them on screen as they arrive floods the dispatcher and the app stops answering, so
        // while the burst lasts the rows are released a batch at a time and the header shows
        // progress instead of the list pretending to be finished.
        // ---------------------------------------------------------------------

        private const int BatchSize = 20;
        private static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(140);

        private bool _batchRendering;
        private bool _batchLoopRunning;
        private bool _batchCompleted;
        private int _batchProcessed;
        private int _batchTotal;
        private int _batchVisibleTarget;

        /// <summary>
        /// Builds a fresh <see cref="NewChatDialogViewModel"/> per New Chat dialog
        /// (clean phone/error state).
        /// </summary>
        private readonly INewChatDialogViewModelFactory _newChatFactory;

        public ChatListViewModel(
            IWhatsAppService whatsAppService,
            IChatStateStore chatState,
            IMessageService messageService,
            IContactService contactService,
            IConnectionService connectionService,
            IHistoryService historyService,
            IShortcutService shortcutService,
            IChatStore chatStore,
            IDispatcher dispatcher,
            IStringResources strings,
            IShellThemeService theme,
            IStatusBarService statusBar,
            IDialogService dialogService,
            INewChatDialogViewModelFactory newChatFactory,
            IChatService chatService = null)
        {
            _chatService = chatService;
            _whatsAppService = whatsAppService;
            _chatState = chatState ?? throw new ArgumentNullException(nameof(chatState));
            _messageService = messageService ?? throw new ArgumentNullException(nameof(messageService));
            _contactService = contactService ?? throw new ArgumentNullException(nameof(contactService));
            _connection = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
            _history = historyService ?? throw new ArgumentNullException(nameof(historyService));
            _shortcutService = shortcutService ?? throw new ArgumentNullException(nameof(shortcutService));
            _chatStore = chatStore;
            _dispatcher = dispatcher;
            _strings = strings;
            _statusBar = statusBar;
            _dialogService = dialogService;
            _newChatFactory = newChatFactory ?? throw new ArgumentNullException(nameof(newChatFactory));

            // Strategy-driven: WhatsApp always inline; Unison Mobile uses StatusBar.
            DisplaySync = theme == null || theme.DisplaySyncInChatList;

            VisibleChats = new ObservableCollection<ChatItem>();

            // The overlay starts on: a cold start has nothing to show and the alternative is a
            // blank pane. The key is the one the markup used to carry as x:Uid.
            _isLoadingOverlayVisible = true;
            _loadingOverlayText = _strings == null
                ? string.Empty
                : _strings.Get("ChatList_Loading.Text", "Preparing conversations...");

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
            SetChatPinnedCommand = new RelayCommand<ChatPinRequest>(
                request => _ = SetChatPinnedAsync(request),
                request => request?.Chat != null &&
                           !string.IsNullOrWhiteSpace(request.Chat.JID) &&
                           _chatService != null);
            SetLocalMuteCommand = new RelayCommand<ChatMuteRequest>(
                request => _ = SetLocalMuteAsync(request),
                request => request?.Chat != null &&
                           !string.IsNullOrWhiteSpace(request.Chat.JID) &&
                           _chatStore != null);
            OpenMenuCommand = new RelayCommand(() => MenuRequested?.Invoke(this, EventArgs.Empty));
            NewChatCommand = new RelayCommand(() => _ = StartNewChatAsync());
        }

        // ---------------------------------------------------------------------
        // What the view binds
        // ---------------------------------------------------------------------

        /// <summary>The rows, already filtered, deduped and ordered. Bind and forget.</summary>
        public ObservableCollection<ChatItem> VisibleChats { get; }

        /// <summary>
        /// When true, sync/connecting text is shown in the chat-list header.
        /// Sourced from <see cref="IShellThemeService.DisplaySyncInChatList"/>.
        /// </summary>
        public bool DisplaySync { get; }

        /// <summary>UI panel should bind this (DisplaySync &amp;&amp; IsSyncStatusVisible).</summary>
        public bool ShowSyncStatusInUi => DisplaySync && IsSyncStatusVisible;

        /// <summary>
        /// The chat the list considers open. Assigned by <see cref="OnChatSelected"/> and by the
        /// restore that follows every refresh; the view mirrors it onto the ListView.
        /// </summary>
        public ChatItem SelectedChat
        {
            get => _selectedChat;
            private set => Set(ref _selectedChat, value);
        }

        /// <summary>True while <see cref="VisibleChats"/> is being reshuffled.</summary>
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
                {
                    ScheduleRefreshVisibleChats();
                }
            }
        }

        public string SyncStatusText
        {
            get => _syncStatusText;
            private set => Set(ref _syncStatusText, value);
        }

        public bool IsSyncStatusVisible
        {
            get => _isSyncStatusVisible;
            private set
            {
                if (Set(ref _isSyncStatusVisible, value))
                {
                    OnPropertyChanged(nameof(ShowSyncStatusInUi));
                }
            }
        }

        /// <summary>Covers the list while there is nothing worth showing yet.</summary>
        public bool IsLoadingOverlayVisible
        {
            get => _isLoadingOverlayVisible;
            private set => Set(ref _isLoadingOverlayVisible, value);
        }

        /// <summary>
        /// What the overlay says. Empty means "use the default from the markup", which is what a
        /// cold start shows before anything has happened.
        /// </summary>
        public string LoadingOverlayText
        {
            get => _loadingOverlayText;
            private set => Set(ref _loadingOverlayText, value);
        }

        /// <summary>Re-queries contact / LID display names from the server for the chat list.</summary>
        public ICommand RefreshContactNamesCommand { get; }

        /// <summary>Wipes local chats/messages (keeps auth) and re-pulls conversation history.</summary>
        public ICommand ResyncConversationsCommand { get; }

        /// <summary>Pins/unpins the chat Start live tile (<see cref="ChatItem.IsWidgetPinned"/>).</summary>
        public ICommand PinChatToStartCommand { get; }

        /// <summary>
        /// Pins/unpins the conversation for the account (<see cref="ChatItem.IsChatPinned"/>),
        /// which is what moves it to the top of the list on the phone as well.
        /// </summary>
        public ICommand SetChatPinnedCommand { get; }

        /// <summary>Sets or clears local/unified <see cref="ChatItem.MutedUntil"/>.</summary>
        public ICommand SetLocalMuteCommand { get; }

        /// <summary>Raises <see cref="MenuRequested"/> so the shell opens settings / overflow.</summary>
        public ICommand OpenMenuCommand { get; }

        /// <summary>Opens the new-chat dialog, creates the chat if needed, then raises <see cref="OpenChatRequested"/>.</summary>
        public ICommand NewChatCommand { get; }

        // ---------------------------------------------------------------------
        // What the view has to be told
        //
        // Everything here is something only the control can carry out: moving the ListView
        // selection, or telling the surrounding shell that the conversation on screen changed.
        // ---------------------------------------------------------------------

        /// <summary>
        /// The selection resolved to this chat after a rebuild. The row may be a different
        /// instance than before - a PN/LID merge replaces it - so the detail pane has to rebind
        /// even when the conversation is nominally the same.
        /// </summary>
        public event EventHandler<ChatItem> SelectionRestored;

        /// <summary>
        /// Local chats are about to be wiped. The shell restores the list pane, since the detail
        /// pane is about to be pointing at nothing.
        /// </summary>
        public event EventHandler BeforeLocalConversationsCleared;

        /// <summary>Header "…" menu — shell listens via ChatListView.MenuClicked.</summary>
        public event EventHandler MenuRequested;

        /// <summary>New-chat flow resolved a JID; the list should open that chat.</summary>
        public event EventHandler<string> OpenChatRequested;

        // ---------------------------------------------------------------------
        // Lifecycle
        // ---------------------------------------------------------------------

        /// <summary>
        /// Starts listening and renders what is already there. Idempotent, and paired with
        /// <see cref="Detach"/>: this view model is created per screen while the facades it
        /// listens to live as long as the app, so a screen that leaves without detaching is
        /// never collected.
        /// </summary>
        public void Attach()
        {
            if (_attached) return;
            _attached = true;

            _connection.StatusChanged += Connection_StatusChanged;
            _history.SyncStatusChanged += History_SyncStatusChanged;
            _history.HistorySyncReceived += History_SyncReceived;
            _history.InitialSyncProgress += History_InitialSyncProgress;
            _contactService.DisplayNamesUpdated += Contacts_DisplayNamesUpdated;
            _chatState.Chats.CollectionChanged += ChatState_ChatsChanged;

            foreach (var chat in _chatState.Chats)
            {
                if (chat == null) continue;
                chat.PropertyChanged -= ChatItem_PropertyChanged;
                chat.PropertyChanged += ChatItem_PropertyChanged;
            }

            IsLoadingOverlayVisible = _chatState.Chats.Count == 0;

            if (_whatsAppService != null && _whatsAppService.IsInitialSyncSafeMode)
            {
                BeginBatchRendering(
                    _whatsAppService.InitialSyncProcessedConversations,
                    _whatsAppService.InitialSyncTotalConversations);
            }

            _ = PresentFromConnectionStatusAsync(_connection.CurrentStatus);

            if (!_batchRendering)
            {
                RefreshVisibleChats();
            }
        }

        /// <summary>Mirror of <see cref="Attach"/>.</summary>
        public void Detach()
        {
            if (!_attached) return;
            _attached = false;

            _connection.StatusChanged -= Connection_StatusChanged;
            _history.SyncStatusChanged -= History_SyncStatusChanged;
            _history.HistorySyncReceived -= History_SyncReceived;
            _history.InitialSyncProgress -= History_InitialSyncProgress;
            _contactService.DisplayNamesUpdated -= Contacts_DisplayNamesUpdated;
            _chatState.Chats.CollectionChanged -= ChatState_ChatsChanged;

            foreach (var chat in _chatState.Chats)
            {
                if (chat != null) chat.PropertyChanged -= ChatItem_PropertyChanged;
            }

            _refreshCts?.Cancel();
            _batchRendering = false;
        }

        // ---------------------------------------------------------------------
        // Calls from the view
        //
        // Each of these is a user gesture or a control event with the UI part already handled -
        // what is left is the decision, which is why it lands here.
        // ---------------------------------------------------------------------

        /// <summary>The user picked a row.</summary>
        public void OnChatSelected(ChatItem chat)
        {
            if (chat == null)
            {
                return;
            }

            _lastSelectedChatJid = chat.JID;
            SelectedChat = chat;
        }

        /// <summary>
        /// The list stopped pointing at anything on purpose - back navigation, or a wipe. Not the
        /// same as the ListView reporting null mid-rebuild, which is never a real deselection.
        /// </summary>
        public void ClearSelection()
        {
            _lastSelectedChatJid = null;
            SelectedChat = null;
        }

        /// <summary>
        /// Re-resolves the last selection against the current rows, for when the control lost it
        /// during a rebuild. Returns the chat to select, or null when it is gone for good.
        /// </summary>
        public ChatItem ResolveLastSelection()
        {
            return string.IsNullOrWhiteSpace(_lastSelectedChatJid)
                ? null
                : FindVisibleByJid(_lastSelectedChatJid);
        }

        /// <summary>
        /// A row scrolled into view. Avatars are fetched here rather than up front because a
        /// full-list fetch on load costs hundreds of requests for rows nobody will look at.
        /// </summary>
        public void OnRowRealized(ChatItem chat)
        {
            if (chat == null || _batchRendering || IsInSafeMode)
            {
                return;
            }

            _contactService.RequestAvatarRefresh(chat);
        }

        /// <summary>Finds a chat by JID or canonical id anywhere in the source list.</summary>
        public ChatItem FindChatByJid(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return null;
            }

            string canonical = GetCanonical(jid);
            return _chatState.Chats.FirstOrDefault(c =>
                c != null &&
                (string.Equals(c.JID, jid, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(GetCanonical(c.JID), canonical, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>
        /// Forces a chat onto the visible list so it can be selected. Needed when something
        /// outside the list opens a conversation the current search filter hides.
        /// </summary>
        public void EnsureVisible(ChatItem chat)
        {
            if (chat == null)
            {
                return;
            }

            if (!VisibleChats.Contains(chat))
            {
                VisibleChats.Insert(0, chat);
            }

            _lastSelectedChatJid = chat.JID;
            SelectedChat = chat;
        }

        // ---------------------------------------------------------------------
        // Menu actions
        // ---------------------------------------------------------------------

        private async Task StartNewChatAsync()
        {
            string jid = await _dialogService.ShowNewChatDialogAsync(_newChatFactory.Create());
            if (string.IsNullOrEmpty(jid))
            {
                return;
            }

            bool exists = false;
            foreach (ChatItem chat in _chatState.Chats)
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

        /// <summary>
        /// The account's pin, as opposed to the Start tile. Ordering follows from the row, and the
        /// facade writes the row before it goes to the server, so there is nothing to do here
        /// beyond letting the list re-sort once it has.
        /// </summary>
        private async Task SetChatPinnedAsync(ChatPinRequest request)
        {
            if (request?.Chat == null || _chatService == null)
            {
                return;
            }

            try
            {
                await _chatService.SetPinnedAsync(request.Chat, request.Pinned);
                RefreshVisibleChats();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatListViewModel] SetChatPinned failed: " + ex.Message);
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

        private bool CanExecuteMenuAction()
        {
            return !_menuActionBusy && !IsInSafeMode;
        }

        private void RaiseMenuCommandsCanExecuteChanged()
        {
            _refreshContactNamesCommand?.RaiseCanExecuteChanged();
            _resyncConversationsCommand?.RaiseCanExecuteChanged();
        }

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
                await PresentSyncStatusAsync(null, visible: false);
                _menuActionBusy = false;
                RaiseMenuCommandsCanExecuteChanged();
            }
        }

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
                ClearSelection();
                VisibleChats.Clear();
                ShowLoadingOverlay(_strings.Get("ChatList_ResyncCleaningHistory", "Cleaning history..."));
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
                                string preparing = _strings.Get(
                                    "ChatList_ResyncingConversations",
                                    "Re-syncing conversations...");
                                _ = PresentSyncStatusAsync(preparing, visible: true);
                                ShowLoadingOverlay(preparing);
                                RefreshVisibleChats();
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
                await PresentSyncStatusAsync(null, visible: false);
                if (VisibleChats.Count > 0)
                {
                    IsLoadingOverlayVisible = false;
                }

                _menuActionBusy = false;
                RaiseMenuCommandsCanExecuteChanged();
            }
        }

        // ---------------------------------------------------------------------
        // Status text
        // ---------------------------------------------------------------------

        /// <summary>
        /// Shared presentation: header UI when <see cref="DisplaySync"/>;
        /// StatusBar progress only when the Unison Mobile strategy owns it.
        /// </summary>
        private async Task PresentSyncStatusAsync(string message, bool visible)
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

        private async Task PresentFromConnectionStatusAsync(string status)
        {
            if (_batchRendering)
            {
                UpdateBatchProgressText();
                return;
            }

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

        private void ShowLoadingOverlay(string text)
        {
            LoadingOverlayText = text;
            IsLoadingOverlayVisible = true;
        }

        // ---------------------------------------------------------------------
        // Facade events
        // ---------------------------------------------------------------------

        private async void Connection_StatusChanged(object sender, string status)
        {
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
        }

        private async void History_SyncStatusChanged(object sender, string message)
        {
            await _dispatcher.RunAsync(() =>
            {
                if (_batchRendering)
                {
                    UpdateBatchProgressText();
                    return;
                }

                if (_awaitingResyncHistory)
                {
                    // Surface service progress ("Re-syncing...", "Preparing...") instead of
                    // freezing on the wipe banner until the wait completes. The service's own
                    // "Saving chats..." is skipped: it is less specific than what we already show.
                    if (string.IsNullOrEmpty(message) ||
                        string.Equals(message, "Saving chats...", StringComparison.Ordinal))
                    {
                        return;
                    }

                    ShowLoadingOverlay(message);
                    _ = PresentSyncStatusAsync(message, visible: true);
                    return;
                }

                if (!string.IsNullOrEmpty(message))
                    _ = PresentSyncStatusAsync(message, visible: true);
                else
                    _ = PresentSyncStatusAsync(null, visible: false);
            });
        }

        private async void History_SyncReceived(object sender, global::Proto.HistorySync sync)
        {
            await _dispatcher.RunAsync(() =>
            {
                if (_batchRendering)
                {
                    // The burst is over, but the rows already queued still have to be released at
                    // the same pace, so this only marks the end and lets the loop drain.
                    _batchCompleted = true;
                    _batchVisibleTarget = Math.Max(_batchVisibleTarget, _chatState.Chats.Count);
                    EnsureBatchLoop();
                    return;
                }

                _ = PresentSyncStatusAsync(null, visible: false);
                IsLoadingOverlayVisible = false;
                RefreshVisibleChats();
            });
        }

        private async void History_InitialSyncProgress(object sender, InitialSyncProgressEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            await _dispatcher.RunAsync(() =>
            {
                // Safe mode gates the menu actions, and it flips with this event.
                RaiseMenuCommandsCanExecuteChanged();

                if (!e.IsCompleted)
                {
                    BeginBatchRendering(e.ProcessedConversations, e.TotalConversations);
                    return;
                }

                _batchRendering = true;
                _batchCompleted = true;
                _batchProcessed = Math.Max(_batchProcessed, e.ProcessedConversations);
                _batchTotal = Math.Max(_batchTotal, e.TotalConversations);
                _batchVisibleTarget = Math.Max(_batchVisibleTarget, _chatState.Chats.Count);
                UpdateBatchProgressText();
                EnsureBatchLoop();
            });
        }

        private async void Contacts_DisplayNamesUpdated(object sender, EventArgs e)
        {
            await _dispatcher.RunAsync(ScheduleRefreshVisibleChats);
        }

        private async void ChatState_ChatsChanged(object sender, NotifyCollectionChangedEventArgs args)
        {
            // Persisted rows are inserted as one startup batch. Queuing a dispatcher callback and
            // a list mutation for every row produced hundreds of pending UI operations on
            // low-memory phones. Subscribe to the items now and let the single history-sync event
            // rebuild the list once.
            if (_whatsAppService != null && _whatsAppService.IsLoadingPersistedChats)
            {
                TrackItems(args);
                return;
            }

            if (_batchRendering || IsInSafeMode)
            {
                TrackItems(args);
                _batchRendering = true;
                _batchVisibleTarget = Math.Max(_batchVisibleTarget, _chatState.Chats.Count);
                EnsureBatchLoop();
                return;
            }

            await _dispatcher.RunAsync(() =>
            {
                if (_chatState.Chats.Count > 0)
                {
                    IsLoadingOverlayVisible = false;
                }

                TrackItems(args);

                bool applied;
                IsRefreshing = true;
                try
                {
                    applied = TryApplyIncrementalChange(args);
                }
                finally
                {
                    IsRefreshing = false;
                }

                if (!applied)
                {
                    ScheduleRefreshVisibleChats();
                    return;
                }

                // After a move or a remove the selected row may be a different instance; rebind.
                RestoreSelection();
            });
        }

        private void TrackItems(NotifyCollectionChangedEventArgs args)
        {
            if (args?.OldItems != null)
            {
                foreach (ChatItem old in args.OldItems)
                {
                    if (old != null) old.PropertyChanged -= ChatItem_PropertyChanged;
                }
            }

            if (args?.NewItems != null)
            {
                foreach (ChatItem added in args.NewItems)
                {
                    if (added == null) continue;
                    added.PropertyChanged -= ChatItem_PropertyChanged;
                    added.PropertyChanged += ChatItem_PropertyChanged;
                }
            }
        }

        private void ChatItem_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Row bindings repaint themselves. Rebuilding or reordering the whole list for every
            // preview and timestamp change is what created the dispatcher backlog during history
            // sync, so only the two things that can change the *order* or the *membership* of the
            // list get to trigger a refresh.
            if (e.PropertyName == nameof(ChatItem.IsChatPinned) ||
                e.PropertyName == nameof(ChatItem.PinnedTimestamp))
            {
                _ = _dispatcher.RunAsync(ScheduleRefreshVisibleChats);
                return;
            }

            if (string.IsNullOrWhiteSpace(SearchQuery))
            {
                return;
            }

            if (e.PropertyName == nameof(ChatItem.Name) ||
                e.PropertyName == nameof(ChatItem.LastMessage) ||
                e.PropertyName == nameof(ChatItem.Timestamp))
            {
                _ = _dispatcher.RunAsync(ScheduleRefreshVisibleChats);
            }
        }

        // ---------------------------------------------------------------------
        // Building the visible list
        // ---------------------------------------------------------------------

        private void ScheduleRefreshVisibleChats()
        {
            if (_batchRendering || IsInSafeMode)
            {
                return;
            }

            _refreshCts?.Cancel();
            _refreshCts = new CancellationTokenSource();
            var token = _refreshCts.Token;

            Task.Delay(80, token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                _ = _dispatcher.RunAsync(RefreshVisibleChats);
            });
        }

        /// <summary>
        /// Rebuilds the visible order in place: filter by the search box, collapse PN/LID
        /// duplicates, sort pinned first and then by recency.
        /// </summary>
        public void RefreshVisibleChats()
        {
            if (_batchRendering || IsInSafeMode)
            {
                return;
            }

            string query = SearchQuery?.Trim() ?? string.Empty;
            var source = _chatState.Chats.ToList();

            if (!string.IsNullOrEmpty(query))
            {
                source = source.Where(c => MatchesQuery(c, query)).ToList();
            }

            source = SortForDisplay(DeduplicateByCanonicalJid(source));

            IsRefreshing = true;
            try
            {
                for (int i = 0; i < source.Count; i++)
                {
                    var item = source[i];
                    if (i < VisibleChats.Count && ReferenceEquals(VisibleChats[i], item))
                    {
                        continue;
                    }

                    int existingIndex = VisibleChats.IndexOf(item);
                    if (existingIndex >= 0)
                    {
                        VisibleChats.Move(existingIndex, i);
                    }
                    else
                    {
                        VisibleChats.Insert(i, item);
                    }
                }

                while (VisibleChats.Count > source.Count)
                {
                    VisibleChats.RemoveAt(VisibleChats.Count - 1);
                }
            }
            finally
            {
                IsRefreshing = false;
            }

            RestoreSelection();
        }

        private bool MatchesQuery(ChatItem chat, string query)
        {
            if (chat == null)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(chat.Name) &&
                chat.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (!string.IsNullOrEmpty(chat.LastMessage) &&
                chat.LastMessage.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (!string.IsNullOrEmpty(chat.JID) &&
                chat.JID.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            // A row with no name of its own can still be searchable: the display name may come
            // from the address book or from a group's metadata.
            string resolved = _whatsAppService == null
                ? null
                : _whatsAppService.ResolveDisplayName(chat.JID, "search");
            return !string.IsNullOrEmpty(resolved) &&
                   resolved.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// One row per person, even while the same conversation exists under both a phone number
        /// and a LID. Which of the two survives is decided by what it can show: the newest
        /// preview, then a picture over no picture, then a name over no name. Pinning is carried
        /// across either way, because the two rows can receive app-state updates independently
        /// and losing the pin on a merge is visible to the user.
        /// </summary>
        private List<ChatItem> DeduplicateByCanonicalJid(List<ChatItem> source)
        {
            var deduped = new List<ChatItem>();
            var canonicalIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in source)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.JID)) continue;

                string canonical = GetCanonical(item.JID);
                if (!canonicalIndex.TryGetValue(canonical, out var existingIndex))
                {
                    canonicalIndex[canonical] = deduped.Count;
                    deduped.Add(item);
                    continue;
                }

                var existing = deduped[existingIndex];

                if (item.IsChatPinned && !existing.IsChatPinned && existing.PinnedTimestamp != 0)
                {
                    existing.IsChatPinned = true;
                    existing.PinnedTimestamp = item.PinnedTimestamp;
                }
                else if (item.IsChatPinned && existing.IsChatPinned &&
                         (item.PinnedTimestamp ?? 0) > (existing.PinnedTimestamp ?? 0))
                {
                    existing.PinnedTimestamp = item.PinnedTimestamp;
                }

                DateTime existingPreviewUtc = existing.LastMessageTimestampUtc ?? DateTime.MinValue;
                DateTime itemPreviewUtc = item.LastMessageTimestampUtc ?? DateTime.MinValue;
                bool itemHasNewerPreview = itemPreviewUtc > existingPreviewUtc;
                bool samePreviewButBetterAvatar = itemPreviewUtc == existingPreviewUtc &&
                    string.IsNullOrWhiteSpace(existing.GetAvatarUrl(preferHigh: false)) &&
                    !string.IsNullOrWhiteSpace(item.GetAvatarUrl(preferHigh: false));
                bool samePreviewAndAvatarButBetterName = itemPreviewUtc == existingPreviewUtc &&
                    string.Equals(existing.GetAvatarUrl(preferHigh: false), item.GetAvatarUrl(preferHigh: false), StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrWhiteSpace(existing.Name) &&
                    !string.IsNullOrWhiteSpace(item.Name);

                if (itemHasNewerPreview || samePreviewButBetterAvatar || samePreviewAndAvatarButBetterName)
                {
                    if (existing.IsChatPinned && !item.IsChatPinned && item.PinnedTimestamp != 0)
                    {
                        item.IsChatPinned = true;
                        item.PinnedTimestamp = existing.PinnedTimestamp;
                    }

                    deduped[existingIndex] = item;
                }
            }

            return deduped;
        }

        private static List<ChatItem> SortForDisplay(IEnumerable<ChatItem> source)
        {
            return source
                .OrderByDescending(c => c.IsChatPinned)
                .ThenByDescending(c => c.PinnedTimestamp ?? 0)
                .ThenByDescending(c => c.LastMessageTimestampUtc ?? DateTime.MinValue)
                .ThenBy(c => c.Name ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Applies a single source change straight to the visible list instead of rebuilding it.
        /// Only valid without a search filter, where the visible list is the source list; with a
        /// filter, whether a row belongs on screen is a question only a rebuild can answer.
        /// Returns false when the caller should rebuild.
        /// </summary>
        private bool TryApplyIncrementalChange(NotifyCollectionChangedEventArgs args)
        {
            if (args == null || !string.IsNullOrWhiteSpace(SearchQuery))
            {
                return false;
            }

            switch (args.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    return TryInsert(args.NewItems);

                case NotifyCollectionChangedAction.Remove:
                    if (args.OldItems == null) return false;
                    RemoveAll(args.OldItems);
                    return true;

                case NotifyCollectionChangedAction.Move:
                    if (args.NewItems == null || args.NewItems.Count != 1) return false;
                    return MoveToMatchSource(args.NewItems[0] as ChatItem);

                case NotifyCollectionChangedAction.Replace:
                    if (args.OldItems == null || args.NewItems == null) return false;
                    RemoveAll(args.OldItems);
                    return TryInsert(args.NewItems);

                default:
                    return false;
            }
        }

        private bool TryInsert(System.Collections.IList items)
        {
            if (items == null)
            {
                return false;
            }

            foreach (ChatItem item in items)
            {
                if (item == null || VisibleChats.Contains(item))
                {
                    continue;
                }

                int targetIndex = _chatState.Chats.IndexOf(item);
                if (targetIndex < 0)
                {
                    return false;
                }

                VisibleChats.Insert(Math.Min(targetIndex, VisibleChats.Count), item);
            }

            return true;
        }

        private void RemoveAll(System.Collections.IList items)
        {
            foreach (ChatItem item in items)
            {
                if (item == null) continue;
                int index = VisibleChats.IndexOf(item);
                if (index >= 0)
                {
                    VisibleChats.RemoveAt(index);
                }
            }
        }

        private bool MoveToMatchSource(ChatItem chat)
        {
            if (chat == null)
            {
                return false;
            }

            int sourceIndex = _chatState.Chats.IndexOf(chat);
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

        /// <summary>
        /// Points the selection back at the open conversation after the list moved underneath it.
        /// When the row is momentarily gone - a PN/LID merge removes and re-adds it - the JID is
        /// kept so a later change can find it again.
        /// </summary>
        private void RestoreSelection()
        {
            if (string.IsNullOrWhiteSpace(_lastSelectedChatJid))
            {
                return;
            }

            var restored = FindVisibleByJid(_lastSelectedChatJid);
            if (restored == null)
            {
                return;
            }

            _lastSelectedChatJid = restored.JID;
            SelectedChat = restored;
            SelectionRestored?.Invoke(this, restored);
        }

        private ChatItem FindVisibleByJid(string jid)
        {
            string canonical = GetCanonical(jid);
            return VisibleChats.FirstOrDefault(c =>
                c != null &&
                (string.Equals(c.JID, jid, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(GetCanonical(c.JID), canonical, StringComparison.OrdinalIgnoreCase)));
        }

        private string GetCanonical(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return jid;
            }

            string canonical = _whatsAppService == null ? null : _whatsAppService.GetCanonicalJid(jid);
            return string.IsNullOrWhiteSpace(canonical) ? jid : canonical;
        }

        private bool IsInSafeMode =>
            _whatsAppService != null && _whatsAppService.IsInitialSyncSafeMode;

        // ---------------------------------------------------------------------
        // Batch rendering
        // ---------------------------------------------------------------------

        private void BeginBatchRendering(int processed, int total)
        {
            _batchRendering = true;
            _batchCompleted = false;
            _batchProcessed = Math.Max(_batchProcessed, processed);
            _batchTotal = Math.Max(_batchTotal, total);
            _batchVisibleTarget = Math.Max(
                _batchVisibleTarget,
                Math.Min(_chatState.Chats.Count, Math.Max(BatchSize, processed)));

            IsLoadingOverlayVisible = VisibleChats.Count == 0;
            UpdateBatchProgressText();
            EnsureBatchLoop();
        }

        private void EnsureBatchLoop()
        {
            if (_batchLoopRunning)
            {
                return;
            }

            _batchLoopRunning = true;
            _ = RunBatchLoopAsync();
        }

        private async Task RunBatchLoopAsync()
        {
            try
            {
                while (_batchRendering)
                {
                    await Task.Delay(BatchInterval).ConfigureAwait(false);
                    await _dispatcher.RunAsync(ReleaseNextBatch);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatListViewModel] Batch render loop failed: " + ex.Message);
            }
            finally
            {
                _batchLoopRunning = false;
            }
        }

        /// <summary>
        /// Moves at most one batch of conversations from the store onto the screen, newest first,
        /// and stops the loop once everything the source holds is visible.
        /// </summary>
        private void ReleaseNextBatch()
        {
            if (!_batchRendering)
            {
                return;
            }

            var source = SortForDisplay(
                _chatState.Chats.Where(c => c != null && !string.IsNullOrWhiteSpace(c.JID)));

            int desiredCount = _batchCompleted
                ? source.Count
                : Math.Min(source.Count, Math.Max(_batchVisibleTarget, BatchSize));

            var visibleCanonical = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var visible in VisibleChats)
            {
                if (visible == null) continue;
                visibleCanonical.Add(GetCanonical(visible.JID));
            }

            int added = 0;
            foreach (var chat in source)
            {
                if (VisibleChats.Count >= desiredCount || added >= BatchSize)
                {
                    break;
                }

                if (!visibleCanonical.Add(GetCanonical(chat.JID)))
                {
                    continue;
                }

                try
                {
                    _chatStore?.ApplyTo(chat);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[ChatListViewModel] ApplyTo failed: " + ex.Message);
                }

                VisibleChats.Add(chat);
                added++;
            }

            if (VisibleChats.Count > 0)
            {
                IsLoadingOverlayVisible = false;
            }

            if (!_batchCompleted)
            {
                return;
            }

            int uniqueSourceCount = source
                .Select(c => GetCanonical(c.JID))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            if (VisibleChats.Count < uniqueSourceCount)
            {
                return;
            }

            _batchRendering = false;
            _batchCompleted = false;
            RefreshVisibleChats();
            _ = PresentSyncStatusAsync(null, visible: false);
            IsLoadingOverlayVisible = false;
        }

        private void UpdateBatchProgressText()
        {
            if (_batchCompleted)
            {
                _ = PresentSyncStatusAsync(_strings.Get("ChatList_SyncDone"), visible: true);
                LoadingOverlayText = _strings.Get("ChatList_Organizing");
                return;
            }

            string text = _batchTotal > 0
                ? FormatString("ChatList_SyncProgress", _batchProcessed, _batchTotal)
                : FormatString("ChatList_SyncProgressCount", _batchProcessed);

            _ = PresentSyncStatusAsync(text, visible: true);
            LoadingOverlayText = text;
        }

        /// <summary>
        /// A resource string with its placeholders filled. Falls back to the raw string when it
        /// does not match the arguments, so a bad translation shows something rather than throwing
        /// in the middle of a sync.
        /// </summary>
        private string FormatString(string key, params object[] args)
        {
            string format = _strings.Get(key);
            try
            {
                return string.Format(format, args);
            }
            catch (FormatException)
            {
                return format;
            }
        }
    }
}
