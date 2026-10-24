// =============================================================================
// TearDownSocketForSoftReconnectDispatch
//
// Body of TearDownSocketForSoftReconnect. Socket I/O stays on the host.
// =============================================================================
using System;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class TearDownSocketForSoftReconnectDispatch
    {
        public static void Run(ITearDownSocketForSoftReconnectHost host)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            object staleSocket = host.DetachCurrentSocket();
            if (staleSocket == null)
            {
                return;
            }

            host.DisconnectAndDisposeSocket(staleSocket);
        }
    }
}
