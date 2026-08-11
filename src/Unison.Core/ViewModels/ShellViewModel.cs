using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Input;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;

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
        private readonly IConnectionService _connectionService;
        private readonly IProfileService _profileService;
        private readonly IDispatcher _dispatcher;
        private readonly INotificationService _notificationService;
        private readonly IRuntimeDiagnostics _diagnostics;
        private readonly ISystemInfoProvider _systemInfo;
        private readonly INavigator _navigator;
        private readonly IStringResources _strings;

        private bool _isPaneOpen;
        private string _currentUserName;
        private string _currentUserAvatar;
        private string _activeSection = "chats";
        private string _appSurface = SurfaceStartup;
        private string _chatPane = PaneWideBoth;
        private bool _isNarrowWindow;
        private bool _hasActiveChat;
        private ChatItem _pendingChat;
        private bool _initialized;
        private bool _eventsHooked;
        private bool _startInProgress;
        private bool _startPairingOnLoginSurface = true;

        public ShellViewModel(
            IWhatsAppService whatsAppService,
            IConnectionService connectionService,
            IProfileService profileService,
            IDispatcher dispatcher,
            INotificationService notificationService,
            IRuntimeDiagnostics diagnostics,
            ISystemInfoProvider systemInfo,
            INavigator navigator,
            IStringResources strings)
        {
            _whatsAppService = whatsAppService;
            _connectionService = connectionService;
            _profileService = profileService;
            _dispatcher = dispatcher;
            _notificationService = notificationService;
            _diagnostics = diagnostics;
            _systemInfo = systemInfo;
            _navigator = navigator;
            _strings = strings;

            TogglePaneCommand = new RelayCommand(() => IsPaneOpen = !IsPaneOpen);
            NavigateToSectionCommand = new RelayCommand<string>(NavigateToSection);
        }

        public bool IsPaneOpen
        {
            get => _isPaneOpen;
            set => Set(ref _isPaneOpen, value);
        }

        public string CurrentUserName
        {
            get => _currentUserName;
            private set
            {
                if (Set(ref _currentUserName, value))
                {
                    OnPropertyChanged(nameof(ProfileDisplayName));
                }
            }
        }

        /// <summary>Name for profile row; localized "Profile" when empty.</summary>
        public string ProfileDisplayName =>
            string.IsNullOrWhiteSpace(CurrentUserName)
                ? _strings.Get("Shell_Profile.Text", "Profile")
                : CurrentUserName;

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
                    OnPropertyChanged(nameof(ShowSystemBackButton));
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

        public ICommand TogglePaneCommand { get; }
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

            _whatsAppService.OnSessionInitialized += WhatsAppService_OnSessionInitialized;
            _whatsAppService.OnSessionCleared += WhatsAppService_OnSessionCleared;
            _whatsAppService.OnError += WhatsAppService_OnError;
            _whatsAppService.OnUserProfileChanged += WhatsAppService_OnUserProfileChanged;
            if (_connectionService != null)
            {
                _connectionService.ConnectionEnded += ConnectionService_ConnectionEnded;
            }
            _eventsHooked = true;
        }

        /// <summary>
        /// Fast-launch bootstrap: auth first, then Start (welcome) or AppShell.
        /// Login/QR is only reached via Start â†’ Get started, or session wipe.
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
            RefreshUserInfo();
            AppSurface = SurfaceConnected;
            ActiveSection = NavigationRoutes.Chats;
            SyncChatPane();
            OnPropertyChanged(nameof(ShowSystemBackButton));
            try
            {
                _navigator.NavigateAndClear(NavigationRoutes.AppShell);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ShellViewModel] Navigate AppShell failed: " + ex.Message);
            }
        }

        public void EnterStartSurface()
        {
            _startPairingOnLoginSurface = false;
            if (!Set(ref _appSurface, SurfaceStart))
            {
                OnPropertyChanged(nameof(AppSurface));
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
        /// When true, LoginPage starts Connect/QR.
        /// Cleared-session wipe sets false first (show Login), then true (start pairing).
        /// </summary>
        public bool StartPairingOnLoginSurface => _startPairingOnLoginSurface;

        public void EnterLoginSurface(bool startPairing = true)
        {
            _startPairingOnLoginSurface = startPairing;
            // Always notify so LoginPage can restart QR (or defer) even when already on Login.
            if (!Set(ref _appSurface, SurfaceLogin))
            {
                OnPropertyChanged(nameof(AppSurface));
            }

            OnPropertyChanged(nameof(StartPairingOnLoginSurface));

            try
            {
                _navigator.NavigateAndClear(NavigationRoutes.Login);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ShellViewModel] Navigate Login failed: " + ex.Message);
            }
        }

        private void NavigateToSection(string section)
        {
            if (string.IsNullOrWhiteSpace(section))
            {
                return;
            }

            ActiveSection = section;
            try
            {
                if (string.Equals(section, NavigationRoutes.Settings, StringComparison.OrdinalIgnoreCase))
                {
                    _navigator.NavigateInShellAndClear(NavigationRoutes.Settings);
                }
                else if (string.Equals(section, NavigationRoutes.Debug, StringComparison.OrdinalIgnoreCase))
                {
                    _navigator.NavigateInShellAndClear(NavigationRoutes.Debug);
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

            OnPropertyChanged(nameof(ShowSystemBackButton));
        }

        public void ReportWindowNarrow(bool isNarrow)
        {
            // Handset (not Continuum): never switch to desktop dual-pane / Inline open
            // hamburger just because landscape width â‰¥ 720.
            if (IsPhoneHandset())
            {
                isNarrow = true;
            }

            IsNarrowWindow = isNarrow;
            CloseHamburgerIfPhoneHandset();
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
            }
            else
            {
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
                // VSM WideState may open SplitView without changing the VM value â€”
                // force PropertyChanged so MainPage closes the control.
                OnPropertyChanged(nameof(IsPaneOpen));
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

            OnPropertyChanged(nameof(ShowSystemBackButton));
        }

        public void ClearChat()
        {
            PendingChat = null;
            HasActiveChat = false;

            if (IsNarrowWindow)
            {
                ChatPane = PaneNarrowList;
            }

            OnPropertyChanged(nameof(ShowSystemBackButton));
        }

        public void ReportActiveChat(bool hasActiveChat)
        {
            HasActiveChat = hasActiveChat;
            if (IsNarrowWindow)
            {
                SyncChatPane();
            }
            else
            {
                OnPropertyChanged(nameof(ShowSystemBackButton));
            }
        }

        public void SyncChatPane()
        {
            if (IsNarrowWindow)
            {
                ChatPane = HasActiveChat ? PaneNarrowDetail : PaneNarrowList;
            }
            else
            {
                ChatPane = PaneWideBoth;
            }

            OnPropertyChanged(nameof(ShowSystemBackButton));
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
                CurrentUserAvatar = null;
                return;
            }

            CurrentUserName = profile.Name;
            CurrentUserAvatar = profile.AvatarUrl;
        }

        public void Detach()
        {
            if (!_eventsHooked)
            {
                return;
            }

            _whatsAppService.OnSessionInitialized -= WhatsAppService_OnSessionInitialized;
            _whatsAppService.OnSessionCleared -= WhatsAppService_OnSessionCleared;
            _whatsAppService.OnError -= WhatsAppService_OnError;
            _whatsAppService.OnUserProfileChanged -= WhatsAppService_OnUserProfileChanged;
            if (_connectionService != null)
            {
                _connectionService.ConnectionEnded -= ConnectionService_ConnectionEnded;
            }
            _eventsHooked = false;
        }

        private void ConnectionService_ConnectionEnded(object sender, ConnectionEndedEventArgs e)
        {
            // Wipe + toast are owned by IConnectionService (facade).
            // Shell navigates via OnSessionCleared when auto-unlink clears the session.
            if (e == null)
            {
                return;
            }

            Debug.WriteLine(
                "[ShellViewModel] ConnectionEnded reason=" + e.Reason +
                " code=" + e.Code +
                " requiresRelink=" + e.RequiresRelink);
        }

        private void WhatsAppService_OnSessionCleared(object sender, SessionClearedEventArgs e)
        {
            // Raised on UI via WhatsAppService.RaiseSessionClearedAsync â€” keep sync so
            // ClearSessionAsync can show Login before keystore wipe continues.
            bool startPairing = e?.StartPairing ?? true;
            EnterLoginSurface(startPairing);
            ActiveSection = "chats";
            ClearChat();
            ApplyProfile(null);
            try { _notificationService.ClearAll(); } catch { }
        }

        private async void WhatsAppService_OnUserProfileChanged(object sender, EventArgs e)
        {
            await _dispatcher.RunAsync(RefreshUserInfo);
        }

        private async void WhatsAppService_OnSessionInitialized(object sender, EventArgs e)
        {
            await _dispatcher.RunAsync(() =>
            {
                EnterConnectedSurface();
                _whatsAppService.StartDeferredStartupMaintenance();
            });
        }

        private void WhatsAppService_OnError(object sender, Exception ex)
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
                await _dispatcher.RunAsync(RefreshUserInfo);
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
    }
}
