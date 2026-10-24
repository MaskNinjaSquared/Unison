// =============================================================================
// IConnectionSocketSessionFactory
//
// Host (UWP) builds SocketBridge, wires events, and opens WinRT transports.
// Portable ConnectionConnectOpen only calls this port — next step toward a
// connection host that can leave UWP once the session handle is opaque.
// =============================================================================
using System.Threading.Tasks;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Builds and opens one WhatsApp socket session for <c>ConnectAsync</c>.
    /// </summary>
    public interface IConnectionSocketSessionFactory
    {
        /// <summary>
        /// Creates the session, attaches remote hooks + event wiring, returns an opaque handle.
        /// </summary>
        object CreateAndWire(ConnectionSocketSessionRequest request);

        /// <summary>
        /// Loads / reuses the key store on the assigned session.
        /// Returns the persistent key store for the host to keep.
        /// </summary>
        Task<object> InitializeKeyStoreAsync(object session);

        /// <summary>Opens Noise over host-built transports (or legacy connect).</summary>
        Task ConnectTransportAsync(object session);

        /// <summary>Disposes a session handle the host no longer owns.</summary>
        void DisposeSession(object session);
    }
}
