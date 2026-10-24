// =============================================================================
// ConnectionConnectOpen
//
// After prepare: factory builds + wires the session, host assigns it, factory
// inits keys and opens transport, host starts the health monitor.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;

namespace Unison.WhatsAppClient.Helpers
{
    /// <summary>Transport-open phase of <c>ConnectAsync</c>.</summary>
    public static class ConnectionConnectOpen
    {
        public static async Task RunAsync(
            IConnectionConnectOpenHost host,
            IConnectionSocketSessionFactory sockets,
            bool reuseLoadedKeyState)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (sockets == null)
            {
                throw new ArgumentNullException(nameof(sockets));
            }

            ConnectionSocketSessionRequest request =
                host.CreateSocketSessionRequest(reuseLoadedKeyState);
            object session = sockets.CreateAndWire(request);
            host.AssignSocketSession(session);

            object keyStore = await sockets.InitializeKeyStoreAsync(session).ConfigureAwait(false);
            host.ApplyPersistentKeyStore(keyStore, reuseLoadedKeyState);

            if (host.ShouldRefuseConnectBecauseSessionDied)
            {
                host.AbortAssignedSocket("fatal-session-ended-before-socket");
                return;
            }

            try
            {
                await sockets.ConnectTransportAsync(session).ConfigureAwait(false);
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
