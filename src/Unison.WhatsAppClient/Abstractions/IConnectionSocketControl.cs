// =============================================================================
// IConnectionSocketControl
//
// The live socket as the netstandard hosts see it. The transport type stays in
// the UWP host; this port only answers readiness and tears the slot down.
// =============================================================================
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IConnectionSocketControl
    {
        /// <summary>The socket in the slot, or null. Opaque: only this port knows its type.</summary>
        object Current { get; }

        /// <summary>A socket is in the slot, its transport is open and the Noise handshake is done.</summary>
        bool IsConnected { get; }

        /// <summary>True when <paramref name="socket"/> is non-null and still the one in the slot.</summary>
        bool Holds(object socket);

        /// <summary>Empties the socket slot and returns what it held (null when empty).</summary>
        object DetachCurrent();

        /// <summary>Best-effort disconnect and dispose; ignores anything that is not a socket.</summary>
        void DisconnectAndDispose(object socket);

        /// <summary>The auth state <paramref name="socket"/> carries, or null.</summary>
        object AuthOf(object socket);

        /// <summary>Sends the unlink notice on the socket in the slot.</summary>
        Task LogoutAsync(string reason);
    }
}
