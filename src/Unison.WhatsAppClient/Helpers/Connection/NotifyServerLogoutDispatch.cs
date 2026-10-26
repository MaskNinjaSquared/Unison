// =============================================================================
// NotifyServerLogoutDispatch
//
// Body of NotifyServerLogoutAsync. Socket / reconnect flags stay on the host.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class NotifyServerLogoutDispatch
    {
        public static async Task RunAsync(INotifyServerLogoutHost host, string reason = null)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (!host.HasActiveSocket)
            {
                return;
            }

            host.ArmUserLogoutDisconnectFlags();
            host.StopConnectionHealthMonitorForLogout("logout");

            try
            {
                await host.LogoutActiveSocketAsync(reason ?? "user-initiated").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                host.RecordLogoutNoticeFailed(ex);
            }
        }
    }
}
