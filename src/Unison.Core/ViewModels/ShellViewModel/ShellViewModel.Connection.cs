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
                _connectionService.StartDeferredStartupMaintenance();
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
                registered = await _connectionService.IsRegisteredAsync().ConfigureAwait(false);
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
                    _connectionService.StartDeferredStartupMaintenance();
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
                await _connectionService.EnsureConnectedAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ShellViewModel] Background connection failed: {ex}");
                _diagnostics?.Write(
                    "startup",
                    "cold-start-ensure-failed",
                    ex.Message);
                try
                {
                    (_connectionService as IConnectionSoftReconnect)
                        ?.RequestSoftReconnect("cold-start-ensure-failed");
                }
                catch (Exception reconnectEx)
                {
                    Debug.WriteLine(
                        "[ShellViewModel] Soft reconnect after cold-start ensure failed: " +
                        reconnectEx.Message);
                }
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
                await _connectionService.LoadPersistedUiStateAsync();
                StartChatListAddressBookPass();
                if (_chatService != null)
                {
                    _notificationService.UpdateBadge(_chatService.GetTotalUnreadCount());
                }
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

        /// <summary>
        /// Address-book names for the restored 1:1 chats, without waiting for the session.
        /// Off the UI thread: reading the agenda is slow on phones.
        /// </summary>
        private void StartChatListAddressBookPass()
        {
            if (_contactService == null)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await _contactService.RefreshChatListAddressBookNamesAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _diagnostics?.RecordException("startup", "chat-list-address-book-failed", ex);
                    Debug.WriteLine($"[ShellViewModel] Chat-list address-book pass failed: {ex.Message}");
                }
            });
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
