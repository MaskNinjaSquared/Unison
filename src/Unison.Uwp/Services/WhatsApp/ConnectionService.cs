using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
using Unison.Uwp.Services;
using Windows.Networking.Connectivity;

namespace Unison.Uwp.Services.WhatsApp
{
    /// <summary>
    /// Facade: disconnect policy (settings + network) and session wipe / toast.
    /// WhatsAppService reports socket facts; this layer decides and executes.
    /// </summary>
    public sealed class ConnectionService : IConnectionService
    {
        private readonly ILocalSettings _localSettings;
        private readonly INotificationService _notificationService;
        private readonly IStringResources _strings;
        private IWhatsAppService _whatsApp;
        private int _handlingRelink;

        public ConnectionService(
            ILocalSettings localSettings,
            INotificationService notificationService,
            IStringResources strings)
        {
            _localSettings = localSettings ?? throw new ArgumentNullException(nameof(localSettings));
            _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
            _strings = strings ?? throw new ArgumentNullException(nameof(strings));
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
                    Debug.WriteLine("[ConnectionService] HasInternetAccess failed: " + ex.Message);
                    return true;
                }
            }
        }

        public bool AutoUnlinkOnLogoutEnabled =>
            _localSettings.Get<bool>(LocalSettingsConstants.AutoUnlinkOnLogoutEnabled);

        public void AttachWhatsAppService(IWhatsAppService whatsApp)
        {
            _whatsApp = whatsApp;
        }

        public void NotifyStreamError(string code)
        {
            EvaluateAndMaybeUnlink(ClassifyStreamError(code), code, "stream-error");
        }

        public void NotifySuspectedInvalidSession(string trigger)
        {
            EvaluateAndMaybeUnlink(DisconnectReason.LoggedOut, "401", trigger ?? "suspected-invalid");
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

        private void EvaluateAndMaybeUnlink(DisconnectReason reason, string code, string trigger)
        {
            bool online = HasInternetAccess;
            bool autoUnlink = AutoUnlinkOnLogoutEnabled;

            if (!online && IsRelinkReason(reason))
            {
                Debug.WriteLine(
                    "[ConnectionService] Downgrading " + reason + " to Network (offline) trigger=" + trigger);
                reason = DisconnectReason.Network;
                code = string.IsNullOrWhiteSpace(code) ? "offline" : code;
            }

            string message = Describe(reason, code);
            Debug.WriteLine(
                "[ConnectionService] Evaluate code=" + (code ?? "(null)") +
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

            if (args.RequiresRelink && autoUnlink)
            {
                if (Interlocked.Exchange(ref _handlingRelink, 1) == 1)
                {
                    return;
                }

                try
                {
                    _whatsApp?.SuppressReconnectFromPolicy(trigger + ":" + (code ?? ""));
                    _ = ExecuteAutoUnlinkAsync(args);
                }
                catch
                {
                    Interlocked.Exchange(ref _handlingRelink, 0);
                    throw;
                }
            }
            else if (args.RequiresRelink && !autoUnlink)
            {
                RuntimeDiagnosticsService.Instance.Write(
                    "connection",
                    "auto-unlink-skipped",
                    "reason=setting-off; code=" + (code ?? "") + "; trigger=" + trigger);
            }

            try
            {
                ConnectionEnded?.Invoke(this, args);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ConnectionService] ConnectionEnded handler failed: " + ex.Message);
            }
        }

        private async Task ExecuteAutoUnlinkAsync(ConnectionEndedEventArgs e)
        {
            try
            {
                Debug.WriteLine("[ConnectionService] Auto-unlink executing reason=" + e.Reason + " code=" + e.Code);

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
                    Debug.WriteLine("[ConnectionService] Logged-out toast failed: " + toastEx.Message);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ConnectionService] Auto-unlink failed: " + ex.Message);
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
