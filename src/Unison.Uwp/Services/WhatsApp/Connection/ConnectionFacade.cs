using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
using Unison.Uwp.Services;
using Unison.Uwp.Services.Socket;
using Windows.Networking.Connectivity;

namespace Unison.Uwp.Services.WhatsApp.Connection
{
    /// <summary>
    /// Facade: disconnect policy (settings + network) and session wipe / toast.
    /// WhatsAppService reports socket facts; this layer decides and executes.
    /// </summary>
    public sealed class ConnectionFacade : IConnectionService
    {
        private readonly ILocalSettings _localSettings;
        private readonly INotificationService _notificationService;
        private readonly IStringResources _strings;
        private readonly IWhatsAppSessionProvider _sessions;
        private readonly ILocalContactsService _localContacts;
        private IWhatsAppService _whatsApp;
        private int _handlingRelink;

        internal ConnectionFacade(
            ILocalSettings localSettings,
            INotificationService notificationService,
            IStringResources strings,
            IWhatsAppSessionProvider sessions,
            ILocalContactsService localContacts)
        {
            _localSettings = localSettings ?? throw new ArgumentNullException(nameof(localSettings));
            _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
            _strings = strings ?? throw new ArgumentNullException(nameof(strings));
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _localContacts = localContacts;
        }

        public event EventHandler<ConnectionEndedEventArgs> ConnectionEnded;

        public bool HasInternetAccess
        {
            get
            {
                try
                {
                    var profile = NetworkInformation.GetInternetConnectionProfile();
                    if (profile == null)
                    {
                        return false;
                    }

                    var level = profile.GetNetworkConnectivityLevel();
                    return level == NetworkConnectivityLevel.InternetAccess ||
                           level == NetworkConnectivityLevel.ConstrainedInternetAccess;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[ConnectionFacade] HasInternetAccess failed: " + ex.Message);
                    return true;
                }
            }
        }

        public bool AutoUnlinkOnLogoutEnabled =>
            _localSettings.Get<bool>(LocalSettingsConstants.AutoUnlinkOnLogoutEnabled);

        public void AttachWhatsAppService(IWhatsAppService whatsApp)
        {
            if (ReferenceEquals(_whatsApp, whatsApp))
            {
                return;
            }

            UnhookClient(_whatsApp);
            _whatsApp = whatsApp;
            HookClient(whatsApp);
        }

        public void NotifyStreamError(string code)
        {
            EvaluateAndMaybeUnlink(ClassifyStreamError(code), code, "stream-error");
        }

        /// <summary>
        /// Reported as a bad session rather than a logout on purpose. This is a guess made from
        /// the shape of the failures - repeated closes before login - and a guess is not grounds
        /// for deleting credentials that the phone may still consider perfectly valid. It stops
        /// the reconnect loop and leaves the decision to unlink with the user.
        /// </summary>
        public void NotifySuspectedInvalidSession(string trigger)
        {
            EvaluateAndMaybeUnlink(DisconnectReason.BadSession, "500", trigger ?? "suspected-invalid");
        }

        public DisconnectReason ClassifyStreamError(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return DisconnectReason.Unknown;
            }

            switch (code.Trim())
            {
                case "401":
                case "device_removed":
                case "device-removed":
                    return DisconnectReason.LoggedOut;
                case "403":
                    return DisconnectReason.Forbidden;
                case "408":
                case "428":
                    return DisconnectReason.Network;
                case "440":
                    return DisconnectReason.ConnectionReplaced;
                case "500":
                    return DisconnectReason.BadSession;
                case "515":
                    return DisconnectReason.RestartRequired;
                case "offline":
                    return DisconnectReason.Network;
                default:
                    return DisconnectReason.Unknown;
            }
        }

        public async Task LogoutAsync(string reason = null)
        {
            var whatsApp = _whatsApp;
            if (whatsApp == null)
            {
                return;
            }

            RuntimeDiagnosticsService.Instance.Write(
                "connection",
                "logout-requested",
                "reason=" + (reason ?? "user-initiated"));

            await whatsApp.NotifyServerLogoutAsync(reason).ConfigureAwait(false);
            await whatsApp.ClearSessionAsync().ConfigureAwait(false);
            if (_localContacts != null)
            {
                try
                {
                    await _localContacts.ClearPublishedAppContactsAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[ConnectionFacade] Clearing Windows People list failed: " + ex.Message);
                }
            }
        }

        private void EvaluateAndMaybeUnlink(DisconnectReason reason, string code, string trigger)
        {
            bool online = HasInternetAccess;
            bool autoUnlink = AutoUnlinkOnLogoutEnabled;

            // 401/403 are the server refusing the credentials. An offline reading of the
            // network profile must not turn that into "try again" — the account is gone
            // on the phone whether Wi‑Fi looks up or not. 440/500 can still be a blip.
            if (!online && IsRelinkReason(reason) &&
                reason != DisconnectReason.LoggedOut &&
                reason != DisconnectReason.Forbidden)
            {
                Debug.WriteLine(
                    "[ConnectionFacade] Downgrading " + reason + " to Network (offline) trigger=" + trigger);
                reason = DisconnectReason.Network;
                code = string.IsNullOrWhiteSpace(code) ? "offline" : code;
            }

            string message = Describe(reason, code);
            Debug.WriteLine(
                "[ConnectionFacade] Evaluate code=" + (code ?? "(null)") +
                " reason=" + reason +
                " online=" + online +
                " autoUnlink=" + autoUnlink +
                " trigger=" + trigger);

            RuntimeDiagnosticsService.Instance.Write(
                "connection",
                "disconnect-evaluated",
                "code=" + (code ?? "") +
                "; reason=" + reason +
                "; online=" + online +
                "; autoUnlink=" + autoUnlink +
                "; trigger=" + trigger);

            var args = new ConnectionEndedEventArgs(reason, code, message);

            // Backing off and unlinking are separate decisions. A connection that was taken over
            // (440) is not worth retrying, but the device is still linked on the phone.
            // 401 / device_removed is the opposite: the phone already removed this companion.
            if (args.ShouldStopReconnecting)
            {
                _whatsApp?.SuppressReconnectFromPolicy(trigger + ":" + (code ?? ""));
            }

            if (args.RequiresRelink)
            {
                if (!autoUnlink)
                {
                    RuntimeDiagnosticsService.Instance.Write(
                        "connection",
                        "auto-unlink-forced",
                        "reason=explicit-logout; setting-off=true; code=" + (code ?? "") +
                        "; trigger=" + trigger);
                }

                if (Interlocked.Exchange(ref _handlingRelink, 1) == 1)
                {
                    return;
                }

                try
                {
                    _ = ExecuteAutoUnlinkAsync(args);
                }
                catch
                {
                    Interlocked.Exchange(ref _handlingRelink, 0);
                    throw;
                }
            }

            try
            {
                ConnectionEnded?.Invoke(this, args);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ConnectionFacade] ConnectionEnded handler failed: " + ex.Message);
            }
        }

        private async Task ExecuteAutoUnlinkAsync(ConnectionEndedEventArgs e)
        {
            try
            {
                Debug.WriteLine("[ConnectionFacade] Auto-unlink executing reason=" + e.Reason + " code=" + e.Code);

                if (_whatsApp != null)
                {
                    await _whatsApp.ClearSessionAsync().ConfigureAwait(false);
                }

                string title = _strings.Get("Toast_LoggedOutTitle", "Session unlinked");
                string body = e.Reason == DisconnectReason.ConnectionReplaced
                    ? _strings.Get(
                        "Toast_SessionReplacedBody",
                        "Another WhatsApp Web session took over. Scan the QR code again.")
                    : _strings.Get(
                        "Toast_LoggedOutBody",
                        "The session was disconnected on your phone. Scan the QR code to link again.");

                try
                {
                    _notificationService.ShowToast(title, body);
                }
                catch (Exception toastEx)
                {
                    Debug.WriteLine("[ConnectionFacade] Logged-out toast failed: " + toastEx.Message);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ConnectionFacade] Auto-unlink failed: " + ex.Message);
                RuntimeDiagnosticsService.Instance.RecordException(
                    "connection",
                    "auto-unlink-failed",
                    ex);
            }
            finally
            {
                Interlocked.Exchange(ref _handlingRelink, 0);
            }
        }

        // ---------------------------------------------------------------------
        // Pairing
        //
        // Forwarding rather than re-deciding: the client raises these, and this only puts them
        // where the pairing screen can reach them without knowing the client exists.
        // ---------------------------------------------------------------------

        public event EventHandler<string> QrReceived;

        public event EventHandler QrExpired;

        public event EventHandler<string> StatusChanged;

        public event EventHandler SessionEstablished;

        public event EventHandler<SessionClearedEventArgs> SessionCleared;

        public event EventHandler<Exception> Failed;

        public string CurrentStatus
        {
            get
            {
                var whatsApp = _whatsApp;
                return whatsApp == null ? null : whatsApp.CurrentConnectionStatus;
            }
        }

        public Task StartPairingAsync()
        {
            var whatsApp = _whatsApp;
            return whatsApp == null ? Task.CompletedTask : whatsApp.ConnectAsync();
        }

        public async Task<string> RequestPairingCodeAsync(string phoneNumber)
        {
            if (string.IsNullOrWhiteSpace(phoneNumber))
            {
                return null;
            }

            var session = _sessions.Current;
            if (session == null || !session.Connection.IsConnected)
            {
                // The hello IQ only works on a live unregistered socket, same as rc14
                // waiting for the QR event before requestPairingCode.
                await StartPairingAsync().ConfigureAwait(false);
                session = await WaitForConnectedSessionAsync().ConfigureAwait(false);
            }

            if (session == null || !session.Connection.IsConnected)
            {
                Debug.WriteLine("[ConnectionFacade] Pairing by phone number is unavailable: socket is not connected");
                return null;
            }

            return await session.RequestPairingCodeAsync(phoneNumber).ConfigureAwait(false);
        }

        private async Task<Unison.Socket.Session.WhatsAppSession> WaitForConnectedSessionAsync()
        {
            for (var i = 0; i < 40; i++)
            {
                var session = _sessions.Current;
                if (session != null && session.Connection.IsConnected)
                {
                    return session;
                }

                await Task.Delay(250).ConfigureAwait(false);
            }

            return _sessions.Current;
        }

        public Task ClearLocalSessionAsync()
        {
            var whatsApp = _whatsApp;
            return whatsApp == null ? Task.CompletedTask : whatsApp.ClearSessionAsync();
        }

        private void HookClient(IWhatsAppService whatsApp)
        {
            if (whatsApp == null)
            {
                return;
            }

            whatsApp.OnQRCodeReceived += Client_OnQrCodeReceived;
            whatsApp.OnQrExpired += Client_OnQrExpired;
            whatsApp.OnConnectionUpdate += Client_OnConnectionUpdate;
            whatsApp.OnSessionInitialized += Client_OnSessionInitialized;
            whatsApp.OnSessionCleared += Client_OnSessionCleared;
            whatsApp.OnError += Client_OnError;
        }

        private void UnhookClient(IWhatsAppService whatsApp)
        {
            if (whatsApp == null)
            {
                return;
            }

            whatsApp.OnQRCodeReceived -= Client_OnQrCodeReceived;
            whatsApp.OnQrExpired -= Client_OnQrExpired;
            whatsApp.OnConnectionUpdate -= Client_OnConnectionUpdate;
            whatsApp.OnSessionInitialized -= Client_OnSessionInitialized;
            whatsApp.OnSessionCleared -= Client_OnSessionCleared;
            whatsApp.OnError -= Client_OnError;
        }

        private void Client_OnQrCodeReceived(object sender, string qr)
        {
            Raise(() => QrReceived?.Invoke(this, qr), "QrReceived");
        }

        private void Client_OnQrExpired(object sender, EventArgs e)
        {
            Raise(() => QrExpired?.Invoke(this, EventArgs.Empty), "QrExpired");
        }

        private void Client_OnConnectionUpdate(object sender, string status)
        {
            Debug.WriteLine("[ConnectionFacade] StatusChanged → " + (status ?? "<null>"));
            Raise(() => StatusChanged?.Invoke(this, status), "StatusChanged");
        }

        private void Client_OnSessionInitialized(object sender, EventArgs e)
        {
            Raise(() => SessionEstablished?.Invoke(this, EventArgs.Empty), "SessionEstablished");
        }

        private void Client_OnSessionCleared(object sender, SessionClearedEventArgs e)
        {
            Raise(() => SessionCleared?.Invoke(this, e), "SessionCleared");
        }

        private void Client_OnError(object sender, Exception error)
        {
            Raise(() => Failed?.Invoke(this, error), "Failed");
        }

        /// <summary>
        /// A subscriber that throws must not take the client's event loop down with it, which is
        /// what would happen without this: these are raised from the socket's own threads.
        /// </summary>
        private static void Raise(Action raise, string name)
        {
            try
            {
                raise();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ConnectionFacade] " + name + " handler failed: " + ex.Message);
            }
        }

        private static bool IsRelinkReason(DisconnectReason reason)
        {
            return reason == DisconnectReason.LoggedOut ||
                   reason == DisconnectReason.ConnectionReplaced ||
                   reason == DisconnectReason.BadSession ||
                   reason == DisconnectReason.Forbidden;
        }

        private static string Describe(DisconnectReason reason, string code)
        {
            switch (reason)
            {
                case DisconnectReason.LoggedOut:
                    return "Logged out (401). Session is invalid - please re-link your device.";
                case DisconnectReason.ConnectionReplaced:
                    return "Connection replaced (440). Another device connected with your session.";
                case DisconnectReason.Forbidden:
                    return "Forbidden (403). Access denied to this resource.";
                case DisconnectReason.BadSession:
                    return "Bad session (500).";
                case DisconnectReason.RestartRequired:
                    return "Restart required (515). Reconnecting...";
                case DisconnectReason.Network:
                    return "Connection lost (" + code + ").";
                default:
                    return string.IsNullOrWhiteSpace(code)
                        ? "Connection ended."
                        : "Stream error " + code;
            }
        }
    }
}
