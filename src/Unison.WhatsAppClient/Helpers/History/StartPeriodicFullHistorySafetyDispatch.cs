// =============================================================================
// StartPeriodicFullHistorySafetyDispatch
//
// Body of StartPeriodicFullHistorySafety. ConnectionHealthMonitorOwner owns
// CTS / Task.Run; loop body stays on IConnectionHealthMonitorIoHost.
// =============================================================================
using System;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class StartPeriodicFullHistorySafetyDispatch
    {
        public static void Run(IStartPeriodicFullHistorySafetyHost host)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            host.StopPeriodicFullHistorySafety("restart");
            host.StartPeriodicFullHistorySafetyLoop();
            host.LogPeriodicFullHistorySafetyStarted(
                host.PeriodicFullHistorySafetyIntervalMinutes);
        }
    }
}
