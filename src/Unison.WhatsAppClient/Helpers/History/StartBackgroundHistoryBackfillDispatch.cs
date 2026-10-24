// =============================================================================
// StartBackgroundHistoryBackfillDispatch
//
// Body of StartBackgroundHistoryBackfillAsync. Lock / CAS stay on the host.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class StartBackgroundHistoryBackfillDispatch
    {
        public static async Task<int> RunAsync(IStartBackgroundHistoryBackfillHost host, string reason)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (!await host.TryAcquireHistoryHydrationAsync().ConfigureAwait(false))
            {
                host.LogHistoryHydrationSkippedAlreadyRunning(reason);
                return 0;
            }

            host.SetHistoryHydrationActive(1);
            try
            {
                return await host.RunHistoryHydrationBackfillCoreAsync(reason).ConfigureAwait(false);
            }
            finally
            {
                host.SetHistoryHydrationActive(0);
                host.ReleaseHistoryHydration();
            }
        }
    }
}
