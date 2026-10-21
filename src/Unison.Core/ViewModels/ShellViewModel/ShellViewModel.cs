using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Input;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;

namespace Unison.Core.ViewModels
{
    /// <summary>
    /// App shell surface (start / login / connected) and chat list/detail pane intent.
    /// Root navigation via <see cref="INavigator"/>; shell sections via NavigateInShell.
    /// </summary>
    public partial class ShellViewModel : Observable
    {
        public const string SurfaceStartup = "Startup";
        public const string SurfaceStart = "Start";
        public const string SurfaceLogin = "Login";
        public const string SurfaceConnected = "Connected";

        public const string PaneWideBoth = "WideBoth";
        public const string PaneNarrowList = "NarrowList";
        public const string PaneNarrowDetail = "NarrowDetail";

        /// <summary>The chat list, read from its owner rather than from the service that fills it.</summary>
        private readonly IChatStateStore _chatState;

        /// <summary>Launch order, registration and the socket. The shell drives all three.</summary>
        private readonly IConnectionService _connectionService;

        /// <summary>Only for the unread total behind the tile badge.</summary>
        private readonly IChatService _chatService;
        private readonly IProfileService _profileService;
        private readonly IDispatcher _dispatcher;
        private readonly INotificationService _notificationService;
        private readonly IShortcutService _shortcutService;
        private readonly IRuntimeDiagnostics _diagnostics;
        private readonly ISystemInfoProvider _systemInfo;
        private readonly INavigator _navigator;
        private readonly IStringResources _strings;
        private readonly ILocalSettings _localSettings;
        private readonly ISessionLogger _sessionLogger;
        private readonly IBackgroundAccessPrompt _backgroundAccessPrompt;

        /// <summary>Only for the startup address-book pass over the restored chat list.</summary>
        private readonly IContactService _contactService;

        private bool _isPaneOpen;
        private string _currentUserName;
        private string _currentUserPhone;
        private string _currentUserAvatar;
        private string _activeSection = "chats";
        private string _appSurface = SurfaceStartup;
        private string _chatPane = PaneWideBoth;
        private bool _isNarrowWindow;
        private bool _hasActiveChat;
        private bool _statusDetailOpen;
        private ChatItem _pendingChat;
        private string _pendingOpenChatJid;
        private string _pendingOpenProfileJid;
        /// <summary>Survives <see cref="ClearPendingOpenProfileJid"/> so Legacy overlay can re-Init.</summary>
        private string _lastQueuedProfileJid;
        private readonly ChatListDeepLinkHoldGate _deepLinkHold;
        private bool _initialized;
        private bool _eventsHooked;
        private bool _startInProgress;
        private bool _startPairingOnLoginSurface = true;
        private double _chatListPaneWidth;
        private readonly NotificationCounterWatch _notificationCounter;

        /// <summary>
        /// When true, Enter* / NavigateToAppShell update state but skip root Frame navigation
        /// (BootView plays the exit animation, then calls <see cref="FinishBootRootNavigation"/>).
        /// </summary>
        public bool SuppressRootNavigation { get; set; }

        public ShellViewModel(
            IChatStateStore chatState,
            IConnectionService connectionService,
            IChatService chatService,
            IProfileService profileService,
            IDispatcher dispatcher,
            INotificationService notificationService,
            IShortcutService shortcutService,
            IRuntimeDiagnostics diagnostics,
            ISystemInfoProvider systemInfo,
            INavigator navigator,
            IStringResources strings,
            ILocalSettings localSettings,
            ISessionLogger sessionLogger = null,
            IBackgroundAccessPrompt backgroundAccessPrompt = null,
            IContactService contactService = null,
            ChatListDeepLinkHoldGate deepLinkHold = null)
        {
            _chatState = chatState ?? throw new ArgumentNullException(nameof(chatState));
            _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
            _chatService = chatService;
            _profileService = profileService;
            _dispatcher = dispatcher;
            _notificationService = notificationService;
            _shortcutService = shortcutService;
            _diagnostics = diagnostics;
            _systemInfo = systemInfo;
            _navigator = navigator;
            _strings = strings;
            _localSettings = localSettings ?? throw new ArgumentNullException(nameof(localSettings));
            _sessionLogger = sessionLogger;
            _backgroundAccessPrompt = backgroundAccessPrompt;
            _contactService = contactService;
            _deepLinkHold = deepLinkHold;

            _chatListPaneWidth = ReadStoredChatListPaneWidth();

            TogglePaneCommand = new RelayCommand(() => IsPaneOpen = !IsPaneOpen);
            NavigateToSectionCommand = new RelayCommand<string>(NavigateToSection);
            OpenProfileSettingsCommand = new RelayCommand(OpenProfileSettings);

            _navigator.ShellNavigated += Navigator_ShellNavigated;
            _notificationCounter = new NotificationCounterWatch(_chatState.Chats);
            _notificationCounter.Changed += (s, e) =>
                RaiseProperties(nameof(NotificationCounterBadge), nameof(NotificationCounterBadgeText));
        }

        /// <summary>How many chats have a notification. Same count the chat-list header shows.</summary>
        public int NotificationCounterBadge => _notificationCounter?.Count ?? 0;

        /// <summary>Parenthetical next to the Legacy brand, empty when nothing is unread.</summary>
        public string NotificationCounterBadgeText =>
            NotificationCounter.FormatBadge(NotificationCounterBadge);

        private void PairingTrace(string message)
        {
            string line = "[Pairing/Shell] " + (message ?? string.Empty);
            try
            {
                _sessionLogger?.WriteAlways(line);
            }
            catch
            {
            }

            Debug.WriteLine(line);
        }

        private void Navigator_ShellNavigated(object sender, string route)
        {
            if (string.IsNullOrWhiteSpace(route))
            {
                return;
            }

            if (!string.Equals(ActiveSection, route, StringComparison.OrdinalIgnoreCase))
            {
                ActiveSection = route;
            }

            // Back from Settings/Debug can leave Overlay closed while VM still thinks open
            // (light-dismiss during navigation). Force closed on handset after any shell nav.
            if (IsPhoneHandset() && _isPaneOpen)
            {
                IsPaneOpen = false;
            }

            RaiseSystemBackButtonChanged();
        }

        public bool IsPaneOpen
        {
            get => _isPaneOpen;
            set => Set(ref _isPaneOpen, value);
        }

        /// <summary>
        /// Chat-list column width in WideBoth (persisted via <see cref="LocalSettingsConstants.ChatListPaneWidth"/>).
        /// </summary>
        public double ChatListPaneWidth
        {
            get => _chatListPaneWidth;
            set
            {
                double clamped = ClampChatListPaneWidth(value);
                if (Set(ref _chatListPaneWidth, clamped))
                {
                    try
                    {
                        _localSettings.Set(LocalSettingsConstants.ChatListPaneWidth, clamped);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("[ShellViewModel] Save ChatListPaneWidth failed: " + ex.Message);
                    }
                }
            }
        }

        public string CurrentUserName
        {
            get => _currentUserName;
            private set
            {
                if (Set(ref _currentUserName, value))
                {
                    RaiseProfileDisplayNameChanged();
                }
            }
        }

        /// <summary>Push name, or the account phone when the name is still unknown.</summary>
        public string ProfileDisplayName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(CurrentUserName))
                {
                    return CurrentUserName;
                }

                if (!string.IsNullOrWhiteSpace(CurrentUserPhone))
                {
                    return CurrentUserPhone;
                }

                return _strings.Get("Shell_Profile.Text", "Profile");
            }
        }

        public string CurrentUserPhone
        {
            get => _currentUserPhone;
            private set
            {
                if (Set(ref _currentUserPhone, value))
                {
                    RaiseProfileDisplayNameChanged();
                }
            }
        }

        public string CurrentUserAvatar
        {
            get => _currentUserAvatar;
            private set => Set(ref _currentUserAvatar, value);
        }

        public string ActiveSection
        {
            get => _activeSection;
            set => Set(ref _activeSection, value);
        }

        /// <summary>
        /// Legacy search box, Enter. The open pivot decides the target: chats and archived
        /// write that list's query; the other pivots have nothing to search yet.
        /// </summary>
        public void InvokeFromPivotCommand(string pivotTag, string query, ChatListViewModel target)
        {
            if (!LegacyPivotSearch.AppliesToChatList(pivotTag) || target == null)
            {
                return;
            }

            target.SearchQuery = query?.Trim() ?? string.Empty;
        }

        /// <summary>Startup | Login | Connected — maps to AppSurfaceStates.</summary>
        public string AppSurface
        {
            get => _appSurface;
            private set => Set(ref _appSurface, value);
        }

        /// <summary>
        /// Fired when leaving the authenticated shell (logout / session wipe / return to QR).
        /// Hosts should drop chat detail state immediately — NavigationCache must not keep it.
        /// </summary>
        public event EventHandler SessionUiResetRequested;

        /// <summary>
        /// Raised when leaving Login for Connected — UI plays exit animation, then
        /// calls <see cref="CompleteEnterConnectedNavigation"/>.
        /// </summary>
        public event EventHandler LoginExitTransitionRequested;

        /// <summary>WideBoth | NarrowList | NarrowDetail — maps to ChatPaneStates.</summary>
        public string ChatPane
        {
            get => _chatPane;
            private set
            {
                if (Set(ref _chatPane, value))
                {
                    OnPropertyChanged(nameof(HidesLegacyShellChrome));
                }
            }
        }

        public bool IsNarrowWindow
        {
            get => _isNarrowWindow;
            private set
            {
                if (Set(ref _isNarrowWindow, value))
                {
                    SyncChatPane();
                    OnPropertyChanged(nameof(HidesLegacyShellChrome));
                }
            }
        }

        /// <summary>
        /// WhatsApp Legacy Minimal / narrow: detail fullscreen (chat or status) —
        /// shell brand + pivot headers should hide until the user leaves the detail.
        /// Status detail still renders inside the pivot <c>StatusView</c>; the shell keeps
        /// the pivot body visible and only collapses headers (see ShellView.ApplyShellChrome).
        /// </summary>
        public bool HidesLegacyShellChrome =>
            IsNarrowWindow &&
            string.Equals(ChatPane, PaneNarrowDetail, StringComparison.Ordinal);

        public bool HasActiveChat
        {
            get => _hasActiveChat;
            private set
            {
                if (Set(ref _hasActiveChat, value))
                {
                    // Minimal: never leave an empty chat space open when the active chat drops.
                    if (!value &&
                        IsNarrowWindow &&
                        string.Equals(ChatPane, PaneNarrowDetail, StringComparison.Ordinal))
                    {
                        ChatPane = PaneNarrowList;
                    }

                    RaiseSystemBackButtonChanged();
                }
            }
        }

        public bool ShowSystemBackButton =>
            string.Equals(ActiveSection, NavigationRoutes.Debug, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ActiveSection, NavigationRoutes.Settings, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ActiveSection, NavigationRoutes.Profile, StringComparison.OrdinalIgnoreCase) ||
            (IsNarrowWindow && string.Equals(ChatPane, PaneNarrowDetail, StringComparison.Ordinal)) ||
            (!IsNarrowWindow && HasActiveChat);

        /// <summary>
        /// Status viewer open/close — same ChatPane NarrowDetail/NarrowList flip as <see cref="SelectChat"/>.
        /// </summary>
        public void SelectStatusDetail(bool open)
        {
            bool wasOpen = _statusDetailOpen;
            _statusDetailOpen = open;
            if (IsNarrowWindow)
            {
                ChatPane = open ? PaneNarrowDetail : PaneNarrowList;
            }

            if (wasOpen != open)
            {
                OnPropertyChanged(nameof(HasOpenStatusDetail));
                OnPropertyChanged(nameof(HidesLegacyShellChrome));
            }

            RaiseSystemBackButtonChanged();
        }

        /// <summary>Entering Status shell page: default to list (or WideBoth).</summary>
        public void EnterStatusSurface()
        {
            if (_statusDetailOpen)
            {
                _statusDetailOpen = false;
                OnPropertyChanged(nameof(HasOpenStatusDetail));
                OnPropertyChanged(nameof(HidesLegacyShellChrome));
            }

            SyncStatusPane();
        }

        /// <summary>Leaving Status: restore chat list/detail pane rules.</summary>
        public void LeaveStatusSurface()
        {
            if (_statusDetailOpen)
            {
                _statusDetailOpen = false;
                OnPropertyChanged(nameof(HasOpenStatusDetail));
                OnPropertyChanged(nameof(HidesLegacyShellChrome));
            }

            // ActiveSection may still be Status during NavigatedFrom — do not call SyncChatPane.
            if (IsNarrowWindow)
            {
                bool showDetail = HasActiveChat && PendingChat != null;
                ChatPane = showDetail ? PaneNarrowDetail : PaneNarrowList;
            }
            else
            {
                ChatPane = PaneWideBoth;
            }

            RaiseSystemBackButtonChanged();
        }

        public bool HasOpenStatusDetail => _statusDetailOpen;

        /// <summary>
        /// Chat the page should open in ChatDetail (set by SelectChat / cleared by ClearChat).
        /// </summary>
        public ChatItem PendingChat
        {
            get => _pendingChat;
            private set => Set(ref _pendingChat, value);
        }

        /// <summary>
        /// JID queued from secondary tile / toast launch (<c>chat=</c> arg).
        /// Cleared by <see cref="ClearPendingOpenChatJid"/> after ChatsView opens it.
        /// </summary>
        public string PendingOpenChatJid => _pendingOpenChatJid;

        /// <summary>
        /// JID queued from People ContactPanel / <c>profile=</c> launch.
        /// Cleared by <see cref="ClearPendingOpenProfileJid"/> after ProfileView loads.
        /// </summary>
        public string PendingOpenProfileJid => _pendingOpenProfileJid;

        /// <summary>
        /// Last queued profile JID (kept after pending is cleared) for shell overlay re-Init.
        /// </summary>
        public string LastQueuedProfileJid => _lastQueuedProfileJid;

        /// <summary>
        /// Queues the full-page Profile for a Windows contact / <c>profile=</c> deep link.
        /// Returns true when a profile JID was found (caller should not also open a chat).
    }
}
