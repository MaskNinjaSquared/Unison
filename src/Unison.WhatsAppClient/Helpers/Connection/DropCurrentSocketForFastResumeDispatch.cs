// =============================================================================
// DropCurrentSocketForFastResumeDispatch
//
// Body of DropCurrentSocketForFastResume. Socket I/O stays on the host.
// =============================================================================
using System;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class DropCurrentSocketForFastResumeDispatch
    {
        public static void Run(IDropCurrentSocketForFastResumeHost host)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            object socket = host.TakeCurrentSocketOrNull();
            if (socket == null)
            {
                return;
            }

            bool previousSuppress = host.SuppressReconnect;
            host.SuppressReconnect = true;
            try
            {
                host.DisconnectAndDisposeSocket(socket);
            }
            finally
            {
                host.SuppressReconnect = previousSuppress;
            }
        }
    }
}
