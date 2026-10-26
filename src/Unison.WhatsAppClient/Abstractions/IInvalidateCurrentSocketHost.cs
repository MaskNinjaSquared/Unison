// =============================================================================
// IInvalidateCurrentSocketHost
//
// Side effects for InvalidateCurrentSocketDispatch.
// =============================================================================

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IInvalidateCurrentSocketHost
    {
        bool IsCurrentSocket(object socket);

        void StopConnectionHealthMonitor(string reason);

        void ClearCurrentSocketField();

        void DisconnectAndDisposeSocket(object socket);

        void WriteSocketInvalidatedJournal(string reason);
    }
}
