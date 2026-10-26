// =============================================================================
// ITearDownSocketForSoftReconnectHost
//
// Side effects for TearDownSocketForSoftReconnectDispatch.
// =============================================================================

namespace Unison.WhatsAppClient.Abstractions
{
    public interface ITearDownSocketForSoftReconnectHost
    {
        /// <summary>Clears the current socket field; returns the prior instance (or null).</summary>
        object DetachCurrentSocket();

        void DisconnectAndDisposeSocket(object socket);
    }
}
