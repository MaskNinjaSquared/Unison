// =============================================================================
// StartConnectionHealthMonitorDispatch
//
// Body of StartConnectionHealthMonitor. ConnectionHealthMonitorOwner owns
// CTS / Task.Run; loop bodies stay on IConnectionHealthMonitorIoHost.
// =============================================================================
using System;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class StartConnectionHealthMonitorDispatch
    {
        public static void Run(IStartConnectionHealthMonitorHost host, object socket)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            host.StopConnectionHealthMonitor("restart");
            if (socket == null)
            {
                return;
            }

            host.StartConnectionHealthLoop(socket);
            host.StartPeriodicFullHistorySafety();
        }
    }
}
