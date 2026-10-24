// =============================================================================
// RaiseSessionClearedDispatch
//
// Body of RaiseSessionClearedAsync. History notify / UI event stay on the host.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class RaiseSessionClearedDispatch
    {
        public static async Task RunAsync(IRaiseSessionClearedHost host, bool startPairing)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            host.ResetFirstSyncListHoldFlags("session-cleared");
            try
            {
                host.NotifyHistorySessionCleared();
            }
            catch (Exception ex)
            {
                host.LogSessionCleared(
                    "NotifySessionCleared failed: " + ex.Message);
            }

            await host.RaiseOnSessionClearedOnUiAsync(startPairing).ConfigureAwait(false);
        }
    }
}
