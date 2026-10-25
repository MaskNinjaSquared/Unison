// =============================================================================
// NotifyServerLogoutOwner
//
// Owns INotifyServerLogoutHost. Socket dispose / reconnect flags stay as ports.
// =============================================================================
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class NotifyServerLogoutOwner : INotifyServerLogoutHost
    {
        private readonly Func<bool> _hasActiveSocket;
        private readonly Action _armUserLogoutFlags;
        private readonly Action<string> _stopHealthMonitor;
        private readonly Func<string, Task> _logoutSocketAsync;
        private readonly Action<Exception> _recordFailed;

        public NotifyServerLogoutOwner(
            Func<bool> hasActiveSocket,
            Action armUserLogoutFlags,
            Action<string> stopHealthMonitor,
            Func<string, Task> logoutSocketAsync,
            Action<Exception> recordFailed = null)
        {
            _hasActiveSocket = hasActiveSocket
                ?? throw new ArgumentNullException(nameof(hasActiveSocket));
            _armUserLogoutFlags = armUserLogoutFlags
                ?? throw new ArgumentNullException(nameof(armUserLogoutFlags));
            _stopHealthMonitor = stopHealthMonitor
                ?? throw new ArgumentNullException(nameof(stopHealthMonitor));
            _logoutSocketAsync = logoutSocketAsync
                ?? throw new ArgumentNullException(nameof(logoutSocketAsync));
            _recordFailed = recordFailed;
        }

        public Task RunAsync(string reason = null) =>
            NotifyServerLogoutDispatch.RunAsync(this, reason);

        bool INotifyServerLogoutHost.HasActiveSocket => _hasActiveSocket();

        void INotifyServerLogoutHost.ArmUserLogoutDisconnectFlags() =>
            _armUserLogoutFlags();

        void INotifyServerLogoutHost.StopConnectionHealthMonitorForLogout(string reason) =>
            _stopHealthMonitor(reason);

        Task INotifyServerLogoutHost.LogoutActiveSocketAsync(string reason) =>
            _logoutSocketAsync(reason);

        void INotifyServerLogoutHost.RecordLogoutNoticeFailed(Exception error)
        {
            try
            {
                if (_recordFailed != null)
                {
                    _recordFailed(error);
                    return;
                }
            }
            catch
            {
            }

            Debug.WriteLine(
                "[NotifyServerLogoutOwner] Logout notice failed: " +
                (error != null ? error.Message : string.Empty));
        }
    }
}
