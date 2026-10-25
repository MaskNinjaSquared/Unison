using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Unison.Core.Constants;
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
    /// the user should see - it needs the list filter, the search filter, the PN/LID dedupe and the
    /// pinned-first ordering applied, and none of that is layout, so none of it belongs in a view.
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
    public partial class ChatListViewModel : Observable
    {
        /// <summary>Which JIDs are the same person (PN vs LID).</summary>
        private readonly IJidResolver _jids;

        /// <summary>
        /// Where the chat list comes from. Read through the store rather than through the service,
        /// so this view model no longer depends on which class happens to be producing the data.
        /// </summary>
        private readonly IChatStateStore _chatState;

        private readonly IMessageService _messageService;
        private readonly IContactService _contactService;

        /// <summary>
        /// Community hub titles prefer cached group subjects over contact-only lookup.
        /// </summary>
        private readonly IGroupNaming _groupNaming;

        /// <summary>Transport state for the header line: connecting, open, reconnecting.</summary>
        private readonly IConnectionService _connection;

        /// <summary>Sync progress and the status sentence that goes with it.</summary>
        private readonly IHistoryService _history;

        private readonly IShortcutService _shortcutService;
        private readonly IChatStore _chatStore;
        private readonly ISystemInfoProvider _systemInfo;
        private readonly IChatService _chatService;
        private readonly IGroupService _groups;
        private readonly IDispatcher _dispatcher;
        private readonly IStringResources _strings;
        private readonly IStatusBarService _statusBar;
        private readonly IDialogService _dialogService;
        private readonly ISessionLogger _sessionLogger;
        private readonly IRuntimeDiagnostics _diagnostics;
        private readonly ILocalSettings _localSettings;
        private readonly SyncWorkloadStrategy _workload;
        private readonly ChatListInitialPaintGate _listPaintGate;
        private readonly ChatListDeepLinkHoldGate _deepLinkHold;
        private readonly ChatTimelineLoadPriorityGate _timelinePriority;
        private readonly IAppVisibility _appVisibility;
        /// <summary>True after the ListView has painted once this attach cycle (warm toast must not unload it).</summary>
        private bool _listEverMaterialized;

        /// <summary>
        /// JID labels already asked of the contact service during search. The strip's mention
        /// fallback and the nameless-row fallback both go through <see cref="IContactService.ResolveDisplayName"/>,
        /// which reads the address book, the person cache and the push-name table. One answer
        /// per JID is enough until names are published again.
        /// </summary>
        private readonly Dictionary<string, string> _searchNameCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private CancellationTokenSource _refreshCts;
        private CancellationTokenSource _refreshContinueCts;

        /// <summary>
        /// Desired visible order held across budgeted continue turns so each slice only Moves
        /// instead of re-running projection/sort on the UI thread.
        /// </summary>
        private List<ChatItem> _pendingVisibleRefreshSource;
        private string _lastSyncBannerLog;

        /// <summary>
        /// Sync banner deferred while toast/tile deep-link holds the list â€” flushed on release.
        /// </summary>
        private string _pendingDeepLinkSyncMessage;
        private bool _pendingDeepLinkSyncVisible;

        /// <summary>
        /// Visible refresh deferred while chat-open owns the timeline priority gate.
        /// </summary>
        private bool _refreshDeferredForTimelinePriority;

        /// <summary>
        /// Visible refresh deferred while CoreWindow is hidden (location-keepalive background).
        /// </summary>
        private bool _refreshDeferredForWindowHidden;

        /// <summary>Breadcrumbs for Mobile post-sync ListView materialize crashes (flushed to disk).</summary>
        private int _postSyncReleaseTicks;
        private int _postSyncRowRealized;

        private string _searchQuery;
        private ChatListFilter _activeFilter = ChatListFilter.All;
        private ChatListScope _scope = ChatListScope.Active;
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

        private static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(140);

        /// <summary>
        /// How long one release turn may hold the UI thread past the guaranteed first batch.
        /// Under a 60Hz frame, so a turn that runs long still cannot drop a frame of input.
        /// </summary>
        private const int BatchBudgetMs = 8;

        private int BatchSize => _workload.ChatListReleaseBatchSize;

        private int PreviewHydrateBatchSize => _workload.PreviewHydrateBatchSize;
        private static readonly TimeSpan VisibleRefreshDebounceGenerous = TimeSpan.FromMilliseconds(80);
        private static readonly TimeSpan VisibleRefreshDebounceFrugal = TimeSpan.FromMilliseconds(320);
        /// <summary>Longer coalesce after name-apply storms so one refresh covers many titles.</summary>
        private static readonly TimeSpan NamesAppliedRefreshDebounceFrugal = TimeSpan.FromMilliseconds(480);
        private const int VisibleRefreshMoveBudgetGenerous = 24;
        private const int VisibleRefreshMoveBudgetFrugal = 10;
        /// <summary>Empty VisibleChats → full store: fewer Continue rounds on reattach.</summary>
        private const int VisibleRefreshMoveBudgetFrugalCold = 20;
        private static readonly TimeSpan PreviewHydrateInterval = TimeSpan.FromMilliseconds(40);
        private static readonly TimeSpan MessagePreviewReconcileDebounce = TimeSpan.FromMilliseconds(350);
        private readonly object _messagePreviewReconcileGate = new object();
        private List<string> _messagePreviewReconcileQueue;
        private CancellationTokenSource _messagePreviewReconcileCts;

        private bool _batchRendering;
        private bool _batchLoopRunning;
        private bool _batchCompleted;
        private int _batchProcessed;
        private int _batchTotal;
        private int _batchVisibleTarget;

        /// <summary>
        /// A pin arrived while the debounced refresh was suppressed (batch / safe mode). The batch
        /// loop owns the list then, so the reorder waits for its next tick instead of being lost.
        /// </summary>
        private bool _visibleOrderDirty;

        /// <summary>Raw <see cref="SyncPhaseStatus"/> token from history (not localized).</summary>
        private string _lastSyncPhaseToken;

        /// <summary>
        /// Frugal devices defer visible reshuffles while names/avatars/groups enrich, then run one
        /// refresh when the phase clears.
        /// </summary>
        private bool _refreshDeferredForEnrichment;

        /// <summary>
        /// Reattach started with VisibleChats empty — keep cold budget/Continue gaps until filled.
        /// </summary>
        private bool _visibleColdPopulate;

        private bool _previewHydrateLoopRunning;
        private readonly object _previewHydrateGate = new object();
        private List<HistoryChatPreview> _previewHydrateQueue;

        /// <summary>
        /// Builds a fresh <see cref="NewChatDialogViewModel"/> per New Chat dialog
        /// (clean phone/error state).
        /// </summary>
        private readonly INewChatDialogViewModelFactory _newChatFactory;

        public ChatListViewModel(
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
            IJidResolver jids,
            IChatService chatService = null,
            IGroupService groupService = null,
            IGroupNaming groupNaming = null,
            ISessionLogger sessionLogger = null,
            IRuntimeDiagnostics diagnostics = null,
            ILocalSettings localSettings = null,
            SyncWorkloadStrategy workload = null,
            ChatListInitialPaintGate listPaintGate = null,
            ChatListDeepLinkHoldGate deepLinkHold = null,
            ChatTimelineLoadPriorityGate timelineLoadPriority = null,
            ISystemInfoProvider systemInfo = null,
            IAppVisibility appVisibility = null)
        {
            _jids = jids ?? throw new ArgumentNullException(nameof(jids));
            _chatService = chatService;
            _groups = groupService;
            _groupNaming = groupNaming;
            _sessionLogger = sessionLogger;
            _diagnostics = diagnostics;
            _localSettings = localSettings;
            _systemInfo = systemInfo;
            _workload = workload ?? HighSyncWorkload.Instance;
            _listPaintGate = listPaintGate;
            _deepLinkHold = deepLinkHold;
            _timelinePriority = timelineLoadPriority;
            _appVisibility = appVisibility;
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
            CloseCommunityHubCommand = new RelayCommand(CloseCommunityHub);

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
                chat => AreLiveTilesAvailable &&
                        chat != null &&
                        !string.IsNullOrWhiteSpace(chat.JID));
            SetChatPinnedCommand = new RelayCommand<ChatPinRequest>(
                request => _ = SetChatPinnedAsync(request),
                request => request?.Chat != null &&
                           !string.IsNullOrWhiteSpace(request.Chat.JID) &&
                           _chatService != null);
            DeleteChatCommand = new RelayCommand<ChatItem>(
                chat => _ = DeleteChatAsync(chat),
                chat => chat != null &&
                        !string.IsNullOrWhiteSpace(chat.JID) &&
                        _chatService != null);
            ArchiveChatCommand = new RelayCommand<ChatItem>(
                chat => { },
                chat => chat != null &&
                        !string.IsNullOrWhiteSpace(chat.JID) &&
                        chat.Status == ChatStatus.Active);
            UnarchiveChatCommand = new RelayCommand<ChatItem>(
                chat => { },
                chat => chat != null &&
                        !string.IsNullOrWhiteSpace(chat.JID) &&
                        chat.Status == ChatStatus.Archived);
            SetLocalMuteCommand = new RelayCommand<ChatMuteRequest>(
                request => _ = SetLocalMuteAsync(request),
                request => request?.Chat != null &&
                           !string.IsNullOrWhiteSpace(request.Chat.JID) &&
                           _chatStore != null);
            OpenMenuCommand = new RelayCommand(() => MenuRequested?.Invoke(this, EventArgs.Empty));
            NewChatCommand = new RelayCommand(() => _ = StartNewChatAsync());
            AddContactCommand = new RelayCommand(BeginAddContact);
            FilterChatsCommand = new RelayCommand<int>(ApplyChatListFilter);
        }

        // ---------------------------------------------------------------------
        // What the view binds
        // ---------------------------------------------------------------------

        /// <summary>The rows, already filtered, deduped and ordered. Bind and forget.</summary>
        public ObservableCollection<ChatItem> VisibleChats { get; }

        /// <summary>Community hub open over the list (runtime projection).</summary>
        public ChatItem ActiveCommunity
        {
            get => _activeCommunity;
            private set
            {
                if (ReferenceEquals(_activeCommunity, value))
                {
                    return;
                }

                if (_activeCommunity != null)
                {
                    _activeCommunity.PropertyChanged -= ActiveCommunity_PropertyChanged;
                }

                _activeCommunity = value;
                if (_activeCommunity != null)
                {
                    _activeCommunity.PropertyChanged += ActiveCommunity_PropertyChanged;
                    CommunityHubPresentation.StampLinkedParentDisplayNames(_activeCommunity);
                }

                OnPropertyChanged();
                OnPropertyChanged(nameof(IsCommunityHubOpen));
                OnPropertyChanged(nameof(ActiveCommunityTitle));
                OnPropertyChanged(nameof(ActiveCommunitySubtitle));
            }
        }

        public bool IsCommunityHubOpen => _activeCommunity != null;

        public string ActiveCommunityTitle =>
            _activeCommunity == null
                ? string.Empty
                : CommunityHubPresentation.ResolveHubTitle(
                    _activeCommunity,
                    ResolveCommunityHubLabel,
                    _strings);

        public string ActiveCommunitySubtitle =>
            _activeCommunity == null
                ? string.Empty
                : _activeCommunity.GetCommunitySubtitle(_strings);

        public ICommand CloseCommunityHubCommand { get; }

        private ChatItem _activeCommunity;
        private readonly Dictionary<string, ChatItem> _syntheticCommunities =
            new Dictionary<string, ChatItem>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Community hub JIDs already asked for w:g2 subject/avatar this session.
        /// Avoids re-firing <see cref="IGroupService.RefreshGroupSendPermissionsAsync"/> on every rebuild.
        /// </summary>
        private readonly HashSet<string> _communityHubLabelProbed =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private NotificationCounterWatch _notificationCounter;

        /// <summary>How many chats have a notification. Same count the shell brand shows.</summary>
        public int NotificationCounterBadge => _notificationCounter?.Count ?? 0;

        /// <summary>Parenthetical next to the header, empty when nothing is unread.</summary>
        public string NotificationCounterBadgeText =>
            NotificationCounter.FormatBadge(NotificationCounterBadge);

        public ChatListScope Scope => _scope;

        /// <summary>Sets list membership before event subscriptions and first render.</summary>
        public void ConfigureScope(ChatListScope scope)
        {
            if (_attached)
            {
                if (_scope == scope)
                {
                    return;
                }

                throw new InvalidOperationException("Chat list scope must be configured before Attach().");
            }

            _scope = scope;
            ActiveFilter = ChatListFilter.All;
        }

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

        /// <summary>
        /// Active list filter from the filter flyout. <see cref="ChatListFilter.All"/> means the
        /// list is only constrained by search, if any.
        /// </summary>
        public ChatListFilter ActiveFilter
        {
            get => _activeFilter;
            private set
            {
                if (Set(ref _activeFilter, value))
                {
                    OnPropertyChanged(nameof(IsFilterActive));
                }
            }
        }

        /// <summary>True when a non-All filter is narrowing the list.</summary>
        public bool IsFilterActive => _activeFilter != ChatListFilter.All;

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
        /// False during the first history sync: the list control is not even created (x:Load),
        /// so no template or container exists while the bulk write runs.
        /// </summary>
        public bool IsChatListMaterialized => !IsListHeldForSync;

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

        /// <summary>
        /// Start Live Tile pin UX (false on Windows 11). Null provider â†’ available.
        /// </summary>
        public bool AreLiveTilesAvailable =>
            _systemInfo == null || _systemInfo.LiveTilesCompatible();

        /// <summary>Pins/unpins the chat Start live tile (<see cref="ChatItem.IsWidgetPinned"/>).</summary>
        public ICommand PinChatToStartCommand { get; }

        /// <summary>
        /// Pins/unpins the conversation for the account (<see cref="ChatItem.IsChatPinned"/>),
        /// which is what moves it to the top of the list on the phone as well.
        /// </summary>
        public ICommand SetChatPinnedCommand { get; }

        /// <summary>
        /// Deletes the conversation for the account after asking. Not undoable, which is why the
        /// row does not disappear until the user has confirmed.
        /// </summary>
        public ICommand DeleteChatCommand { get; }

        /// <summary>Placeholder until archive outbound is wired.</summary>
        public ICommand ArchiveChatCommand { get; }

        /// <summary>Placeholder until unarchive outbound is wired.</summary>
        public ICommand UnarchiveChatCommand { get; }

        /// <summary>Sets or clears local/unified <see cref="ChatItem.MutedUntil"/>.</summary>
        public ICommand SetLocalMuteCommand { get; }

        /// <summary>Raises <see cref="MenuRequested"/> so the shell opens settings / overflow.</summary>
        public ICommand OpenMenuCommand { get; }

        /// <summary>Opens the new-chat dialog, creates the chat if needed, then raises <see cref="OpenChatRequested"/>.</summary>
        public ICommand NewChatCommand { get; }

        /// <summary>Legacy app-bar placeholder until add-contact is wired.</summary>
        public ICommand AddContactCommand { get; }

        /// <summary>
        /// Narrows <see cref="VisibleChats"/> by a <see cref="ChatListFilter"/> id from the
        /// flyout. Parameter is an <see cref="int"/> because UWP <c>CommandParameter</c> is not
        /// reliably typed as an enum.
        /// </summary>
        public ICommand FilterChatsCommand { get; }

        private static void BeginAddContact()
        {
        }

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

        /// <summary>Header "â€¦" menu â€” shell listens via ChatListView.MenuClicked.</summary>
        public event EventHandler MenuRequested;

        /// <summary>New-chat flow resolved a JID; the list should open that chat.</summary>
        public event EventHandler<string> OpenChatRequested;

    }
}
