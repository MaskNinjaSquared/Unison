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
    public class ShellViewModel : Observable
    {
        public const string SurfaceStartup = "Startup";
        public const string SurfaceStart = "Start";
        public const string SurfaceLogin = "Login";
        public const string SurfaceConnected = "Connected";

        public const string PaneWideBoth = "WideBoth";
        public const string PaneNarrowList = "NarrowList";
        public const string PaneNarrowDetail = "NarrowDetail";

        private readonly IWhatsAppService _whatsAppService;

        /// <summary>The chat list, read from its owner rather than from the service that fills it.</summary>
        private readonly IChatStateStore _chatState;

        private readonly IConnectionService _connectionService;
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

        private bool _isPaneOpen;
        private string _currentUserName;
        private string _currentUserPhone;
        private string _currentUserAvatar;
        private string _activeSection = "chats";
        private string _appSurface = SurfaceStartup;
        private string _chatPane = PaneWideBoth;
        private bool _isNarrowWindow;
        private bool _hasActiveChat;
        private ChatItem _pendingChat;
        private string _pendingOpenChatJid;
        private bool _initialized;
        private bool _eventsHooked;
        private bool _startInProgress;
        private bool _startPairingOnLoginSurface = true;
        private double _chatListPaneWidth;

        /// <summary>
        /// When true, Enter* / NavigateToAppShell update state but skip root Frame navigation
        /// (BootView plays the exit animation, then calls <see cref="FinishBootRootNavigation"/>).
        /// </summary>
        public bool SuppressRootNavigation { get; set; }

        public ShellViewModel(
            IWhatsAppService whatsAppService,
            IChatStateStore chatState,
            IConnectionService connectionService,
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
            IBackgroundAccessPrompt backgroundAccessPrompt = null)
        {
            _whatsAppService = whatsAppService;
            _chatState = chatState ?? throw new ArgumentNullException(nameof(chatState));
            _connectionService = connectionService;
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

            _chatListPaneWidth = ReadStoredChatListPaneWidth();

            TogglePaneCommand = new RelayCommand(() => IsPaneOpen = !IsPaneOpen);
            NavigateToSectionCommand = new RelayCommand<string>(NavigateToSection);

            _navigator.ShellNavigated += Navigator_ShellNavigated;
        }

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

        /// <summary>Startup | Login | Connected â€” maps to AppSurfaceStates.</summary>
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

        /// <summary>WideBoth | NarrowList | NarrowDetail â€” maps to ChatPaneStates.</summary>
        public string ChatPane
        {
            get => _chatPane;
            private set => Set(ref _chatPane, value);
        }

        public bool IsNarrowWindow
        {
            get => _isNarrowWindow;
            private set
            {
                if (Set(ref _isNarrowWindow, value))
                {
                    SyncChatPane();
                }
            }
        }

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
            string.Equals(ActiveSection, "debug", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ActiveSection, "settings", StringComparison.OrdinalIgnoreCase) ||
            (IsNarrowWindow && string.Equals(ChatPane, PaneNarrowDetail, StringComparison.Ordinal)) ||
            (!IsNarrowWindow && HasActiveChat);

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
        /// Parses activation arguments and queues navigation into the target chat
        /// (narrow → detail pane when open succeeds). Used by secondary tiles and toast clicks.
        /// </summary>
        public void QueueOpenChatFromActivation(string arguments)
        {
            string jid = LaunchActivationHelper.TryGetChatJid(arguments);
            if (string.IsNullOrWhiteSpace(jid))
            {
                return;
            }

            // Prefer navigating onto Chats first so ChatsView is hooked before PropertyChanged.
            if (string.Equals(AppSurface, SurfaceConnected, StringComparison.Ordinal))
            {
                try
                {
                    NavigateToSection(NavigationRoutes.Chats);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[ShellViewModel] Navigate chats for deep link failed: " + ex.Message);
                }
            }

            _pendingOpenChatJid = jid.Trim();
            RaisePendingOpenChatChanged();
            Debug.WriteLine("[ShellViewModel] Queued open chat from activation: " + _pendingOpenChatJid);
        }

        public void ClearPendingOpenChatJid()
        {
            if (_pendingOpenChatJid == null)
            {
                return;
            }

            _pendingOpenChatJid = null;
            RaisePendingOpenChatChanged();
        }

        /// <summary>Opens or closes the shell navigation pane.</summary>
        public ICommand TogglePaneCommand { get; }

        /// <summary>Navigates the shell content frame. Parameter: a <see cref="Constants.NavigationRoutes"/> value.</summary>
        public ICommand NavigateToSectionCommand { get; }

        /// <summary>
        /// Imgur-style: wire service events once. Safe to call repeatedly.
        /// </summary>
        public void Initialize()
        {
            if (_eventsHooked)
            {
                return;
            }

            if (_connectionService != null)
            {
                _connectionService.SessionEstablished += Connection_SessionEstablished;
                _connectionService.SessionCleared += Connection_SessionCleared;
                _connectionService.Failed += Connection_Failed;
                _connectionService.StatusChanged += Connection_StatusChanged;
                _connectionService.ConnectionEnded += ConnectionService_ConnectionEnded;
            }

            if (_profileService != null)
            {
                _profileService.ProfileChanged += Profile_Changed;
            }
            _eventsHooked = true;
        }

        /// <summary>
        /// Fast-launch bootstrap: auth first, then Start (welcome) or AppShell.
        /// Login/QR is only reached via Start → Get started, or session wipe.
        /// Safe to call again (toast / second Boot): re-asserts root surface.
        /// </summary>
        public async Task InitializeAsync()
        {
            Initialize();

            if (_startInProgress)
            {
                return;
            }

            if (_initialized)
            {
                await ResyncRootSurfaceAsync().ConfigureAwait(false);
                return;
            }

            _startInProgress = true;
            AppSurface = SurfaceStartup;

            try
            {
                if (_backgroundAccessPrompt != null &&
                    !await _backgroundAccessPrompt.EnsureOrExitAsync())
                {
                    return;
                }

                await _whatsAppService.InitializeConnectionStateAsync();

                // Cold start: profile from auth / memory (no network).
                ApplyProfile(_profileService.GetCurrentProfile());

                if (await _whatsAppService.IsRegisteredAsync())
                {
                    EnterConnectedSurface();
                    _diagnostics.Write("startup", "fast-connect-dispatched");
                    _ = ConnectInBackgroundAsync();
                    _ = LoadPersistedUiStateInBackgroundAsync();
                    _ = SyncProfileInBackgroundAsync();
                }
                else
                {
                    EnterStartSurface();
                }

                _initialized = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ShellViewModel] InitializeAsync failed: {ex}");
                EnterStartSurface();
            }
            finally
            {
                _startInProgress = false;
            }
        }

        /// <summary>
        /// Re-apply Start / Login / AppShell from current session without re-bootstrap.
        /// </summary>
        private async Task ResyncRootSurfaceAsync()
        {
            try
            {
                if (await _whatsAppService.IsRegisteredAsync())
                {
                    EnterConnectedSurface();
                    return;
                }

                if (string.Equals(AppSurface, SurfaceLogin, StringComparison.Ordinal))
                {
                    EnterLoginSurface(_startPairingOnLoginSurface);
                    return;
                }

                EnterStartSurface();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ShellViewModel] ResyncRootSurface failed: " + ex.Message);
                EnterStartSurface();
            }
        }

        public void EnterConnectedSurface()
        {
            string previous = AppSurface;
            RefreshUserInfo();

            // Stop LoginView from treating AppSurface change as "restart QR".
            _startPairingOnLoginSurface = false;

            // Reconnect / session-initialized must not kick the user out of Settings or Debug.
            bool alreadyConnected = string.Equals(AppSurface, SurfaceConnected, StringComparison.Ordinal);
            AppSurface = SurfaceConnected;

            if (!alreadyConnected)
            {
                ActiveSection = NavigationRoutes.Chats;
            }

            SyncChatPane();
            RaiseSystemBackButtonChanged();
            RaiseLoginPairingFlagChanged();

            // Reconnect / session-initialized while already on AppShell: refresh only.
            // Navigating again remounts ChatsView and drops list + detail selection.
            if (alreadyConnected)
            {
                PairingTrace(
                    "EnterConnectedSurface from=" + (previous ?? "(null)") +
                    " alreadyConnected=True → skip NavigateAndClear (keep shell)");
                return;
            }

            bool fromLogin = string.Equals(previous, SurfaceLogin, StringComparison.Ordinal);
            if (fromLogin)
            {
                EventHandler handler = LoginExitTransitionRequested;
                if (handler != null)
                {
                    PairingTrace(
                        "EnterConnectedSurface from=Login alreadyConnected=" + alreadyConnected +
                        " → defer NavigateAndClear for Login exit animation");
                    try
                    {
                        handler(this, EventArgs.Empty);
                        return;
                    }
                    catch (Exception ex)
                    {
                        PairingTrace("LoginExitTransitionRequested FAILED: " + ex.Message);
                        Debug.WriteLine("[ShellViewModel] Login exit transition failed: " + ex.Message);
                    }
                }
            }

            PairingTrace(
                "EnterConnectedSurface from=" + (previous ?? "(null)") +
                " alreadyConnected=" + alreadyConnected +
                " → NavigateAndClear(AppShell)");
            // From Login without exit-handler: still prefer green Boot bridge.
            if (fromLogin)
            {
                NavigateToBootBridge();
                return;
            }

            NavigateToAppShell();
        }

        /// <summary>
        /// Called by Login UI after the exit wipe animation finishes.
        /// Lands on green Boot bridge, which then opens AppShell.
        /// </summary>
        public void CompleteEnterConnectedNavigation()
        {
            PairingTrace("CompleteEnterConnectedNavigation → NavigateAndClear(Boot postPairing)");
            NavigateToBootBridge();
        }

        /// <summary>
        /// Called by BootView after dwell + exit animation — navigates to the surface
        /// prepared while <see cref="SuppressRootNavigation"/> was set.
        /// </summary>
        public void FinishBootRootNavigation()
        {
            SuppressRootNavigation = false;
            PairingTrace("FinishBootRootNavigation surface=" + (AppSurface ?? "(null)"));

            if (string.Equals(AppSurface, SurfaceStartup, StringComparison.Ordinal))
            {
                PairingTrace("FinishBootRootNavigation skipped (still Startup)");
                return;
            }

            if (string.Equals(AppSurface, SurfaceConnected, StringComparison.Ordinal))
            {
                NavigateToAppShell();
                return;
            }

            if (string.Equals(AppSurface, SurfaceLogin, StringComparison.Ordinal))
            {
                try
                {
                    _navigator.NavigateAndClear(NavigationRoutes.Login);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[ShellViewModel] FinishBoot → Login failed: " + ex.Message);
                }
                return;
            }

            try
            {
                _navigator.NavigateAndClear(NavigationRoutes.Start);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ShellViewModel] FinishBoot → Start failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Called by BootView after the post-QR green bridge dwell (legacy alias).
        /// </summary>
        public void FinishBootToAppShell()
        {
            FinishBootRootNavigation();
        }

        private void NavigateToBootBridge()
        {
            try
            {
                _navigator.NavigateAndClear(NavigationRoutes.Boot, "postPairing");
                PairingTrace("NavigateAndClear(Boot postPairing) OK");
            }
            catch (Exception ex)
            {
                PairingTrace("NavigateAndClear(Boot postPairing) FAILED: " + ex.Message);
                Debug.WriteLine("[ShellViewModel] Navigate Boot bridge failed: " + ex.Message);
                NavigateToAppShell();
            }
        }

        private void NavigateToAppShell()
        {
            if (SuppressRootNavigation)
            {
                PairingTrace("NavigateToAppShell suppressed (Boot exit pending)");
                return;
            }

            try
            {
                _navigator.NavigateAndClear(NavigationRoutes.AppShell);
                PairingTrace("NavigateAndClear(AppShell) OK");
            }
            catch (Exception ex)
            {
                PairingTrace("NavigateAndClear(AppShell) FAILED: " + ex.Message);
                Debug.WriteLine("[ShellViewModel] Navigate AppShell failed: " + ex.Message);
            }
        }

        public void EnterStartSurface()
        {
            ResetAuthenticatedSessionUi();
            _startPairingOnLoginSurface = false;
            if (!Set(ref _appSurface, SurfaceStart))
            {
                RaiseAppSurfaceChanged();
            }

            if (SuppressRootNavigation)
            {
                PairingTrace("EnterStartSurface navigate suppressed (Boot exit pending)");
                return;
            }

            try
            {
                _navigator.NavigateAndClear(NavigationRoutes.Start);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ShellViewModel] Navigate Start failed: " + ex.Message);
            }
        }

        /// <summary>
        /// When true, LoginView starts Connect/QR.
        /// Cleared-session wipe sets false first (show Login), then true (start pairing).
        /// </summary>
        public bool StartPairingOnLoginSurface => _startPairingOnLoginSurface;

        public void EnterLoginSurface(bool startPairing = true)
        {
            bool alreadyOnLogin = string.Equals(_appSurface, SurfaceLogin, StringComparison.Ordinal);
            _startPairingOnLoginSurface = startPairing;

            // Wipe raises SessionCleared twice (show Login, then start QR). The second must
            // only flip the pairing flag — remounting Login looks like a double navigation.
            if (alreadyOnLogin)
            {
                RaiseLoginPairingFlagChanged();
                PairingTrace("EnterLoginSurface already on Login → skip navigate, startPairing=" + startPairing);
                return;
            }

            if (!Set(ref _appSurface, SurfaceLogin))
            {
                RaiseAppSurfaceChanged();
            }

            RaiseLoginPairingFlagChanged();

            if (SuppressRootNavigation)
            {
                PairingTrace("EnterLoginSurface navigate suppressed (Boot exit pending)");
                return;
            }

            // Navigate first. Collapsing the chat pane while AppShell is still on screen
            // is the flash before QR; NavigateAndClear tears the shell down anyway.
            try
            {
                _navigator.NavigateAndClear(NavigationRoutes.Login);
                PairingTrace("EnterLoginSurface NavigateAndClear(Login) startPairing=" + startPairing);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ShellViewModel] Navigate Login failed: " + ex.Message);
            }

            ClearChat();
            ActiveSection = NavigationRoutes.Chats;
            IsPaneOpen = false;
            try
            {
                _navigator?.PurgeShellNavigation();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ShellViewModel] PurgeShellNavigation failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Drop chat chrome + shell navigation so returning to QR cannot revive prior ViewModels.
        /// </summary>
        private void ResetAuthenticatedSessionUi()
        {
            ClearChat();
            ActiveSection = NavigationRoutes.Chats;
            IsPaneOpen = false;

            try
            {
                SessionUiResetRequested?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ShellViewModel] SessionUiResetRequested failed: " + ex.Message);
            }

            try
            {
                _navigator?.PurgeShellNavigation();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ShellViewModel] PurgeShellNavigation failed: " + ex.Message);
            }
        }

        private void NavigateToSection(string section)
        {
            if (string.IsNullOrWhiteSpace(section))
            {
                return;
            }

            // Overlay hamburger: close as soon as a section is chosen (Settings/Debug/Chats).
            if (IsPhoneHandset() && _isPaneOpen)
            {
                IsPaneOpen = false;
            }

            try
            {
                // Navigate BEFORE ActiveSection so MainView.WireChatsViewMenu sees the live Frame.Content.
                // (Setting ActiveSection first wired the previous page, then ShellNavigated skipped
                // re-wire when the route string was already applied.)
                if (string.Equals(section, NavigationRoutes.Settings, StringComparison.OrdinalIgnoreCase))
                {
                    _navigator.NavigateInShell(NavigationRoutes.Settings);
                }
                else if (string.Equals(section, NavigationRoutes.Debug, StringComparison.OrdinalIgnoreCase))
                {
                    _navigator.NavigateInShell(NavigationRoutes.Debug);
                }
                else
                {
                    _navigator.NavigateInShellAndClear(NavigationRoutes.Chats);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ShellViewModel] NavigateInShell failed: " + ex.Message);
            }

            ActiveSection = section;
            RaiseSystemBackButtonChanged();
        }

        /// <summary>
        /// Hardware / system back for shell sections (Settings / Debug → previous shell page).
        /// Chat list/detail back is handled by ChatsView before this is called.
        /// </summary>
        public bool TryHandleShellBack()
        {
            if (_navigator.CanGoBackInShell)
            {
                _navigator.GoBackInShell();
                return true;
            }

            if (string.Equals(ActiveSection, NavigationRoutes.Settings, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ActiveSection, NavigationRoutes.Debug, StringComparison.OrdinalIgnoreCase))
            {
                NavigateToSection(NavigationRoutes.Chats);
                return true;
            }

            return false;
        }

        public void ReportWindowNarrow(bool isNarrow)
        {
            // Handset (not Continuum): never switch to desktop dual-pane / Inline open
            // hamburger just because landscape width ≥ 720.
            if (IsPhoneHandset())
            {
                isNarrow = true;
            }

            bool changed = IsNarrowWindow != isNarrow;
            IsNarrowWindow = isNarrow;
            // Do not auto-close the hamburger here — layout thrash during sync /
            // SizeChanged closed the pane right after the user opened it.
            if (changed)
            {
                SyncChatPane();
            }
        }

        /// <summary>
        /// Call on display orientation change. Closes the overlay hamburger on Mobile
        /// handset; Continuum keeps desktop-like pane behaviour.
        /// </summary>
        public void OnDisplayOrientationChanged()
        {
            if (IsPhoneHandset())
            {
                // Re-assert narrow chat chrome after orientation (chat stays fullscreen).
                ReportWindowNarrow(true);
                CloseHamburgerIfPhoneHandset();
            }
        }

        private bool IsPhoneHandset()
        {
            return _systemInfo != null && _systemInfo.IsMobile() && !_systemInfo.IsContinuum();
        }

        private void CloseHamburgerIfPhoneHandset()
        {
            if (!IsPhoneHandset())
            {
                return;
            }

            if (_isPaneOpen)
            {
                IsPaneOpen = false;
            }
            else
            {
                // VSM Extended may open SplitView without changing the VM value —
                // force PropertyChanged so MainView closes the control.
                RaisePaneOpenChanged();
            }
        }

        public void SelectChat(ChatItem chat)
        {
            PendingChat = chat;
            HasActiveChat = chat != null;

            if (IsNarrowWindow)
            {
                ChatPane = chat != null ? PaneNarrowDetail : PaneNarrowList;
            }

            RaiseSystemBackButtonChanged();
        }

        public void ClearChat()
        {
            PendingChat = null;
            HasActiveChat = false;

            if (IsNarrowWindow)
            {
                ChatPane = PaneNarrowList;
            }

            RaiseSystemBackButtonChanged();
        }

        public void ReportActiveChat(bool hasActiveChat)
        {
            HasActiveChat = hasActiveChat;
            if (!hasActiveChat)
            {
                PendingChat = null;
            }

            if (IsNarrowWindow)
            {
                SyncChatPane();
            }
            else
            {
                RaiseSystemBackButtonChanged();
            }
        }

        public void SyncChatPane()
        {
            if (IsNarrowWindow)
            {
                // Minimal: chat space only with an open intent (PendingChat) and active flag.
                // Do not call EnsureNarrowListWhenNoActiveChat before assigning — that path
                // cleared PendingChat while SelectChat was switching to NarrowDetail.
                bool showDetail = HasActiveChat && PendingChat != null;
                ChatPane = showDetail ? PaneNarrowDetail : PaneNarrowList;
            }
            else
            {
                ChatPane = PaneWideBoth;
            }

            RaiseSystemBackButtonChanged();
        }

        /// <summary>
        /// Minimal / narrow: if the chat space is showing but nothing is selected, force list.
        /// </summary>
        public void EnsureNarrowListWhenNoActiveChat()
        {
            if (!IsNarrowWindow)
            {
                return;
            }

            if (!string.Equals(ChatPane, PaneNarrowDetail, StringComparison.Ordinal))
            {
                return;
            }

            if (HasActiveChat && PendingChat != null)
            {
                return;
            }

            PendingChat = null;
            HasActiveChat = false;
            ChatPane = PaneNarrowList;
            RaiseSystemBackButtonChanged();
        }

        public void RefreshUserInfo()
        {
            ApplyProfile(_profileService.GetCurrentProfile());
        }

        private void ApplyProfile(Profile profile)
        {
            if (profile == null)
            {
                CurrentUserName = null;
                CurrentUserPhone = null;
                CurrentUserAvatar = null;
                return;
            }

            CurrentUserName = profile.Name;
            CurrentUserPhone = profile.Phone;
            CurrentUserAvatar = profile.AvatarUrl;
        }

        public void Detach()
        {
            if (!_eventsHooked)
            {
                return;
            }

            if (_connectionService != null)
            {
                _connectionService.SessionEstablished -= Connection_SessionEstablished;
                _connectionService.SessionCleared -= Connection_SessionCleared;
                _connectionService.Failed -= Connection_Failed;
                _connectionService.StatusChanged -= Connection_StatusChanged;
                _connectionService.ConnectionEnded -= ConnectionService_ConnectionEnded;
            }

            if (_profileService != null)
            {
                _profileService.ProfileChanged -= Profile_Changed;
            }
            _eventsHooked = false;
        }

        private void ConnectionService_ConnectionEnded(object sender, ConnectionEndedEventArgs e)
        {
            // Wipe + toast are owned by IConnectionService (facade).
            // Shell navigates via SessionCleared when auto-unlink clears the session.
            if (e == null)
            {
                return;
            }

            Debug.WriteLine(
                "[ShellViewModel] ConnectionEnded reason=" + e.Reason +
                " code=" + e.Code +
                " requiresRelink=" + e.RequiresRelink);
        }

        private void Connection_SessionCleared(object sender, SessionClearedEventArgs e)
        {
            // Raised on the UI thread by the wipe itself — keep this synchronous so Login is on
            // screen before the keystore wipe continues.
            bool startPairing = e?.StartPairing ?? true;
            EnterLoginSurface(startPairing);
            ApplyProfile(null);
            try { _notificationService.ClearAll(); } catch { }
        }

        private async void Profile_Changed(object sender, EventArgs e)
        {
            await _dispatcher.RunAsync(RefreshUserInfo);
        }

        private async void Connection_SessionEstablished(object sender, EventArgs e)
        {
            PairingTrace("SessionEstablished received (surface=" + AppSurface + ")");
            await _dispatcher.RunAsync(() =>
            {
                PairingTrace("SessionEstablished → EnterConnectedSurface");
                Debug.WriteLine("[ShellViewModel] SessionEstablished → AppShell");
                EnterConnectedSurface();
                _whatsAppService.StartDeferredStartupMaintenance();
            });
        }

        /// <summary>
        /// Safety net: pairing stage-2 (515 restart) or an offline drain can publish
        /// connection updates without SessionEstablished reaching us first (seen on
        /// Mobile — phone shows "synced" while the app is still stuck on the QR page).
        /// Any live-connection status while registered and still on Login/Startup means
        /// we missed the primary signal — leave QR now instead of staying stuck forever.
        /// </summary>
        private async void Connection_StatusChanged(object sender, string status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return;
            }

            bool onLoginOrStartup =
                string.Equals(AppSurface, SurfaceLogin, StringComparison.Ordinal) ||
                string.Equals(AppSurface, SurfaceStartup, StringComparison.Ordinal);
            if (!onLoginOrStartup)
            {
                return;
            }

            string normalized = status.Trim();
            bool liveSignal =
                string.Equals(normalized, "connected", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "open", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "synced", StringComparison.OrdinalIgnoreCase);
            if (!liveSignal)
            {
                return;
            }

            PairingTrace(
                "safety-net probe status=" + normalized +
                " surface=" + AppSurface);

            bool registered;
            try
            {
                registered = await _whatsAppService.IsRegisteredAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                PairingTrace("safety-net IsRegistered FAILED: " + ex.Message);
                Debug.WriteLine("[ShellViewModel] Registered probe failed: " + ex.Message);
                return;
            }

            if (!registered)
            {
                PairingTrace("safety-net SKIP (not registered) status=" + normalized);
                return;
            }

            await _dispatcher.RunAsync(() =>
            {
                // Re-check after the dispatcher hop — SessionEstablished may have won the race.
                if (!string.Equals(AppSurface, SurfaceLogin, StringComparison.Ordinal) &&
                    !string.Equals(AppSurface, SurfaceStartup, StringComparison.Ordinal))
                {
                    PairingTrace(
                        "safety-net SKIP after dispatch (surface already " + AppSurface + ")");
                    return;
                }

                PairingTrace(
                    "safety-net FIRE status=" + normalized +
                    " surface=" + AppSurface + " → EnterConnectedSurface");
                Debug.WriteLine(
                    "[ShellViewModel] ConnectionUpdate '" + normalized + "' while on " +
                    AppSurface + " and registered → AppShell (safety net)");
                EnterConnectedSurface();
                try
                {
                    _whatsAppService.StartDeferredStartupMaintenance();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[ShellViewModel] StartDeferredStartupMaintenance: " + ex.Message);
                }
            });
        }

        private void Connection_Failed(object sender, Exception ex)
        {
            Debug.WriteLine($"[ShellViewModel] Error: {ex?.Message}");
        }

        private async Task ConnectInBackgroundAsync()
        {
            try
            {
                await _whatsAppService.EnsureConnectedAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ShellViewModel] Background connection failed: {ex}");
            }
        }

        private async Task SyncProfileInBackgroundAsync()
        {
            try
            {
                await _profileService.SyncCurrentProfileAsync();
                await _dispatcher.RunAsync(RefreshUserInfo);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ShellViewModel] Background profile sync failed: {ex}");
            }
        }

        private async Task LoadPersistedUiStateInBackgroundAsync()
        {
            try
            {
                await Task.Delay(120);
                await _whatsAppService.LoadPersistedUiStateAsync();
                _notificationService.UpdateBadge(_whatsAppService.GetTotalUnreadCount());
                if (_shortcutService != null)
                {
                    await _shortcutService.RefreshPinnedUnreadAsync(_chatState.Chats);
                }
                await _dispatcher.RunAsync(RefreshUserInfo);

                // Chats may have arrived after ChatsView first Loaded — re-signal deep link.
                if (!string.IsNullOrWhiteSpace(_pendingOpenChatJid))
                {
                    await _dispatcher.RunAsync(() => RaisePendingOpenChatChanged());
                }
            }
            catch (Exception ex)
            {
                _diagnostics.RecordException(
                    "startup",
                    "persisted-ui-load-failed",
                    ex);
                Debug.WriteLine($"[ShellViewModel] Persisted UI load failed: {ex.Message}");
            }
        }

        private double ReadStoredChatListPaneWidth()
        {
            try
            {
                double saved = _localSettings.Get<double>(LocalSettingsConstants.ChatListPaneWidth);
                return ClampChatListPaneWidth(saved);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ShellViewModel] Load ChatListPaneWidth failed: " + ex.Message);
                return ChatPaneLayoutConstants.DefaultListWidth;
            }
        }

        private static double ClampChatListPaneWidth(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return ChatPaneLayoutConstants.DefaultListWidth;
            }

            return Math.Max(
                ChatPaneLayoutConstants.MinListWidth,
                Math.Min(ChatPaneLayoutConstants.MaxListWidth, value));
        }

        private void RaiseSystemBackButtonChanged() =>
            OnPropertyChanged(nameof(ShowSystemBackButton));

        private void RaisePendingOpenChatChanged() =>
            OnPropertyChanged(nameof(PendingOpenChatJid));

        private void RaiseAppSurfaceChanged() =>
            OnPropertyChanged(nameof(AppSurface));

        private void RaiseLoginPairingFlagChanged() =>
            OnPropertyChanged(nameof(StartPairingOnLoginSurface));

        private void RaiseProfileDisplayNameChanged() =>
            OnPropertyChanged(nameof(ProfileDisplayName));

        private void RaisePaneOpenChanged() =>
            OnPropertyChanged(nameof(IsPaneOpen));
    }
}
