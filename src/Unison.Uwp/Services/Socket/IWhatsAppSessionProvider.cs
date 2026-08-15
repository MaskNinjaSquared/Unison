// =============================================================================
// IWhatsAppSessionProvider
//
// Hands out the live Unison.Socket session, or null when there is no connection.
//
// Facades depend on this rather than on a session instance because the socket is
// replaced on every reconnect: one holding the session it was constructed with
// would be sending into a closed one by the second disconnect. Asking each time
// also gives them somewhere to report "not connected" instead of failing in an
// obscure way.
// =============================================================================
using System;
using Unison.Socket.Session;
using Unison.Uwp.Client;

namespace Unison.Uwp.Services.Socket
{
    internal interface IWhatsAppSessionProvider
    {
        /// <summary>The connected session, or null when the legacy stack owns the connection.</summary>
        WhatsAppSession Current { get; }

        /// <summary>
        /// The bridge itself, for the few facades that need the app-side surface rather than the
        /// raw session: the legacy events it republishes, and the modules the session keeps
        /// internal. Null under the same conditions as <see cref="Current"/>.
        /// </summary>
        IWhatsAppSocket Socket { get; }

        bool IsReady { get; }
    }

    /// <summary>
    /// Reads the session out of the connection the service currently holds, when that connection
    /// is the bridge. Asking the service each time rather than caching is deliberate: the socket
    /// is replaced on every reconnect, and a facade holding yesterday's session would send into
    /// a closed one.
    /// </summary>
    internal sealed class BridgeSessionProvider : IWhatsAppSessionProvider
    {
        private readonly Func<IWhatsAppSocket> _socket;

        public BridgeSessionProvider(Func<IWhatsAppSocket> socket)
        {
            if (socket == null)
            {
                throw new ArgumentNullException(nameof(socket));
            }

            _socket = socket;
        }

        public WhatsAppSession Current
        {
            get
            {
                var bridge = _socket() as SocketBridge;
                return bridge != null ? bridge.Session : null;
            }
        }

        public IWhatsAppSocket Socket
        {
            get
            {
                // Only when the bridge is the one connected: under the legacy stack the socket
                // exists but none of what a new-stack facade asks of it does.
                return Current != null ? _socket() : null;
            }
        }

        public bool IsReady
        {
            get
            {
                var session = Current;
                return session != null && session.Connection.IsConnected;
            }
        }
    }
}
