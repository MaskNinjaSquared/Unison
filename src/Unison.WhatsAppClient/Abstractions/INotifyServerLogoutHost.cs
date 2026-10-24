// =============================================================================
// INotifyServerLogoutHost
//
// Side effects for NotifyServerLogoutDispatch.
// =============================================================================
using System;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface INotifyServerLogoutHost
    {
        bool HasActiveSocket { get; }

        void ArmUserLogoutDisconnectFlags();

        void StopConnectionHealthMonitorForLogout(string reason);

        Task LogoutActiveSocketAsync(string reason);

        void RecordLogoutNoticeFailed(Exception error);
    }
}
