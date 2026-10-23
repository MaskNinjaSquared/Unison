// =============================================================================
// ConnectionConnectOpen
//
// After prepare: build + wire handlers, init key store, open transport, start
// health monitor. Handler bodies stay on the host.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;

namespace Unison.WhatsAppClient.Helpers
{
    /// <summary>Transport-open phase of <c>ConnectAsync</c>.</summary>
    public static class ConnectionConnectOpen
    {
        public static async Task RunAsync(
            IConnectionConnectOpenHost host,
            bool reuseLoadedKeyState)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            host.BuildAssignAndWireSocket(reuseLoadedKeyState);
            await host.InitializeKeyStoreAfterAssignAsync(reuseLoadedKeyState).ConfigureAwait(false);

            if (host.ShouldRefuseConnectBecauseSessionDied)
            {
                host.AbortAssignedSocket("fatal-session-ended-before-socket");
                return;
            }

            try
            {
                await host.ConnectAssignedTransportAsync().ConfigureAwait(false);
                host.StartHealthMonitorIfAssignedCurrent();
            }
            catch
            {
                host.ClearAssignedSocketAfterFailedConnect();
                throw;
            }
        }
    }
}
