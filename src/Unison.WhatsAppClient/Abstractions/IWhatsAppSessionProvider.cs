// =============================================================================
// IWhatsAppSessionProvider
//
// Hands out the live Unison.Socket session, or null when there is no connection.
// Facades ask each time rather than caching: the socket is replaced on reconnect.
// =============================================================================
using Unison.Socket.Session;

namespace Unison.WhatsAppClient.Abstractions
{
    /// <summary>Live session access for portable façades (no WinRT / bridge types).</summary>
    public interface IWhatsAppSessionProvider
    {
        /// <summary>The connected session, or null when none is available.</summary>
        WhatsAppSession Current { get; }

        bool IsReady { get; }

        /// <summary>Noise handshake finished; IQ calls are safe.</summary>
        bool IsTransportReady { get; }
    }
}
