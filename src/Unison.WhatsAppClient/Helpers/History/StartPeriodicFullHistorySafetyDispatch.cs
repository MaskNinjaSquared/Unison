// =============================================================================
// StartPeriodicFullHistorySafetyDispatch
//
// Body of StartPeriodicFullHistorySafety. Loop Task.Run stays on the host.
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
