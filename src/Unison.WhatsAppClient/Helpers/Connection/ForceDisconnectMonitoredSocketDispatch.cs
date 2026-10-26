// =============================================================================
// ForceDisconnectMonitoredSocketDispatch
//
// Body of ConnectionHealthMonitorHost.ForceDisconnectMonitoredSocket. Socket
// types stay on the host.
// =============================================================================
using System;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class ForceDisconnectMonitoredSocketDispatch
    {
        public static void Run(IForceDisconnectMonitoredSocketHost host)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (!host.IsMonitoredSocketCurrent)
            {
                return;
            }

            host.DisconnectMonitoredSocketBestEffort();
            host.ClearCurrentSocketReferenceIfStillMonitored();
        }
    }
}
