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
    public partial class ShellViewModel
    {
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

                await _connectionService.InitializeConnectionStateAsync();

                // Cold start: profile from auth / memory (no network).
                ApplyProfile(_profileService.GetCurrentProfile());

                if (await _connectionService.IsRegisteredAsync())
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
                if (await _connectionService.IsRegisteredAsync())
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
                ActiveSection = string.IsNullOrWhiteSpace(_pendingOpenProfileJid)
                    ? NavigationRoutes.Chats
                    : NavigationRoutes.Profile;
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
                    _navigator.NavigateAndClear(NavigationDestination.Login);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[ShellViewModel] FinishBoot → Login failed: " + ex.Message);
                }
                return;
            }

            try
            {
                _navigator.NavigateAndClear(NavigationDestination.Start);
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
                _navigator.NavigateAndClear(NavigationDestination.Boot, "postPairing");
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
                _navigator.NavigateAndClear(NavigationDestination.AppShell);
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
                _navigator.NavigateAndClear(NavigationDestination.Start);
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
                _navigator.NavigateAndClear(NavigationDestination.Login);
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

        private void OpenProfileSettings()
        {
            if (IsPhoneHandset() && _isPaneOpen)
            {
                IsPaneOpen = false;
            }

            try
            {
                _navigator.OpenSettings(true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ShellViewModel] OpenProfileSettings failed: " + ex.Message);
            }
        }
    }
}
