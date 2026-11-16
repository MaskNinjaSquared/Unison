// =============================================================================
// IConnectionSocketControl
//
// The live socket as the netstandard hosts see it. The transport type stays in
// the UWP host; this port only answers readiness and tears the slot down.
// =============================================================================
namespace Unison.WhatsAppClient.Abstractions
{
    public interface IConnectionSocketControl
    {
        /// <summary>A socket is in the slot, its transport is open and the Noise handshake is done.</summary>
        bool IsConnected { get; }

        /// <summary>Empties the socket slot and returns what it held (null when empty).</summary>
        object DetachCurrent();

        /// <summary>Best-effort disconnect and dispose; ignores anything that is not a socket.</summary>
        void DisconnectAndDispose(object socket);
    }
}
