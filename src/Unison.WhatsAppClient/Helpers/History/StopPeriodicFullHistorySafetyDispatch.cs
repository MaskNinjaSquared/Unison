// =============================================================================
// StopPeriodicFullHistorySafetyDispatch
//
// Body of StopPeriodicFullHistorySafety. CTS field stays on the host.
// =============================================================================
using System;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class StopPeriodicFullHistorySafetyDispatch
    {
        public static void Run(IStopPeriodicFullHistorySafetyHost host, string reason)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (!host.TryCancelPeriodicFullHistorySafety())
            {
                return;
            }

            host.LogPeriodicFullHistorySafetyStopped(reason);
        }
    }
}
