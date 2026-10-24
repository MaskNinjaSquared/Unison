// =============================================================================
// IIsCurrentSocketHealthyHost
//
// Side effects for IsCurrentSocketHealthyDispatch.
// =============================================================================
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IIsCurrentSocketHealthyHost
    {
        object CurrentSocket { get; }

        bool SocketHasStalledNodeProcessing(object socket);

        bool SocketHasFreshConnection(object socket);

        bool SocketIsConnected(object socket);

        bool SocketIsHandshakeComplete(object socket);

        int SocketQueuedNodeProcessingCount(object socket);

        void LogNodeQueueStalled(int depth);

        Task<bool> ProbeSocketConnectionAsync(object socket);

        bool SuppressReconnect { get; set; }

        void DisconnectSocket(object socket);

        void ClearCurrentSocketIfSame(object socket);
    }
}
