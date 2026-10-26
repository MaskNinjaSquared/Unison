// =============================================================================
// IsCurrentSocketHealthyDispatch
//
// Body of IsCurrentSocketHealthyAsync. Probe / tear-down stay on the host.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class IsCurrentSocketHealthyDispatch
    {
        public static async Task<bool> RunAsync(IIsCurrentSocketHealthyHost host)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            object socket = host.CurrentSocket;
            var profile = ConnectionHealthProfile.OnDemand;
            bool nodeStalled = socket != null &&
                host.SocketHasStalledNodeProcessing(socket);
            bool connectionFresh = socket != null &&
                host.SocketHasFreshConnection(socket);
            OnDemandSocketHealthStep step = ConnectionHealthPolicy.DecideOnDemand(
                socket != null,
                socket != null && host.SocketIsConnected(socket),
                socket != null && host.SocketIsHandshakeComplete(socket),
                nodeStalled,
                connectionFresh);

            if (step == OnDemandSocketHealthStep.NotReady)
            {
                return false;
            }

            if (step == OnDemandSocketHealthStep.Healthy)
            {
                return true;
            }

            if (step == OnDemandSocketHealthStep.TearDown)
            {
                host.LogNodeQueueStalled(host.SocketQueuedNodeProcessingCount(socket));
            }
            else if (step == OnDemandSocketHealthStep.Probe &&
                     await host.ProbeSocketConnectionAsync(socket).ConfigureAwait(false))
            {
                return true;
            }

            bool previousSuppressReconnect = host.SuppressReconnect;
            host.SuppressReconnect = true;
            try
            {
                host.DisconnectSocket(socket);
            }
            finally
            {
                host.SuppressReconnect = previousSuppressReconnect;
            }

            host.ClearCurrentSocketIfSame(socket);
            return false;
        }
    }
}
