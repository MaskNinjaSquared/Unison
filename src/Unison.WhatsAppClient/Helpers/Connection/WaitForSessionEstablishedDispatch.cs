// =============================================================================
// WaitForSessionEstablishedDispatch
//
// Body of WaitForSessionEstablishedAsync. Gate TCS stays on the host.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class WaitForSessionEstablishedDispatch
    {
        public static async Task RunAsync(IWaitForSessionEstablishedHost host, string reason)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            host.LogWaitingForSession(reason);

            try
            {
                await host.WaitSessionEstablishedGateAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                host.LogSessionGateAborted(reason, ex);
                throw;
            }
        }
    }
}
