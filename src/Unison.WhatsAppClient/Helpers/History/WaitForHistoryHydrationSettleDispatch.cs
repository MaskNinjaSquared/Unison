// =============================================================================
// WaitForHistoryHydrationSettleDispatch
//
// Body of WaitForHistoryHydrationSettleAsync. History request counters stay on
// the host.
// =============================================================================
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class WaitForHistoryHydrationSettleDispatch
    {
        public static async Task RunAsync(IWaitForHistoryHydrationSettleHost host)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            var elapsed = Stopwatch.StartNew();
            TimeSpan budget = host.HydrationSettleBudget;
            while (elapsed.Elapsed < budget && host.IsConnected)
            {
                if (host.HistoryChatsInFlight == 0)
                {
                    await host.DelayAsync(HistoryHydrationSettlePolicy.IdleConfirmDelay)
                        .ConfigureAwait(false);
                    if (host.HistoryChatsInFlight == 0)
                    {
                        return;
                    }
                }

                await host.DelayAsync(HistoryHydrationSettlePolicy.PollDelay)
                    .ConfigureAwait(false);
            }
        }
    }
}
