// =============================================================================
// StopConnectionHealthMonitorDispatch
//
// Body of StopConnectionHealthMonitor. ConnectionHealthMonitorOwner owns CTS.
// =============================================================================
using System;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class StopConnectionHealthMonitorDispatch
    {
        public static void Run(IStopConnectionHealthMonitorHost host, string reason)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (!host.TryCancelConnectionHealthMonitor())
            {
                host.StopPeriodicFullHistorySafety(reason);
                return;
            }

            host.LogConnectionHealthMonitorStopped(reason);
            host.StopPeriodicFullHistorySafety(reason);
        }
    }
}
