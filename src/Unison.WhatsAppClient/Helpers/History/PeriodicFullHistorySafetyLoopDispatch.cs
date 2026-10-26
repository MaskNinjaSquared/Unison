// =============================================================================
// PeriodicFullHistorySafetyLoopDispatch
//
// Body of PeriodicFullHistorySafetyLoopAsync. Task.Run wiring stays on the host.
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class PeriodicFullHistorySafetyLoopDispatch
    {
        public static async Task RunAsync(
            IPeriodicFullHistorySafetyLoopHost host,
            CancellationToken token)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await host.DelayAsync(host.PeriodicFullHistorySafetyInterval, token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                try
                {
                    await host.RunPeriodicFullHistorySafetyTickAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    host.LogPeriodicFullHistorySafetyTickFailed(ex.Message);
                }
            }
        }
    }
}
