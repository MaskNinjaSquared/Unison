// =============================================================================
// IDropCurrentSocketForFastResumeHost
//
// Side effects for DropCurrentSocketForFastResumeDispatch.
// =============================================================================

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IDropCurrentSocketForFastResumeHost
    {
        /// <summary>Null when there is no current socket.</summary>
        object TakeCurrentSocketOrNull();

        bool SuppressReconnect { get; set; }

        void DisconnectAndDisposeSocket(object socket);
    }
}
