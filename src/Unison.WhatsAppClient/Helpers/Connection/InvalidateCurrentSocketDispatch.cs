// =============================================================================
// InvalidateCurrentSocketDispatch
//
// Body of InvalidateCurrentSocket. Socket I/O stays on the host.
// =============================================================================
using System;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class InvalidateCurrentSocketDispatch
    {
        public static void Run(
            IInvalidateCurrentSocketHost host,
            object socket,
            string reason)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (socket == null)
            {
                return;
            }

            if (host.IsCurrentSocket(socket))
            {
                host.StopConnectionHealthMonitor("invalidate:" + reason);
                host.ClearCurrentSocketField();
            }

            host.DisconnectAndDisposeSocket(socket);
            host.WriteSocketInvalidatedJournal(reason);
        }
    }
}
