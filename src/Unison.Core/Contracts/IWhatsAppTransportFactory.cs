// =============================================================================
// IWhatsAppTransportFactory
//
// Host (UWP) creates the concrete StreamSocket / MessageWebSocket transports.
// SocketBridge asks for instances rather than naming WinRT types itself — first
// step toward Init(IWaTransport) / portable connection open.
// =============================================================================

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Builds byte-pipe transports for a WhatsApp connect attempt.
    /// </summary>
    public interface IWhatsAppTransportFactory
    {
        /// <summary>RFC6455 StreamSocket path (broker-capable).</summary>
        IWhatsAppTransport CreateStreamSocketTransport();

        /// <summary>Classic MessageWebSocket path (Mobile default / fallback).</summary>
        IWhatsAppTransport CreateMessageWebSocketTransport();
    }
}
