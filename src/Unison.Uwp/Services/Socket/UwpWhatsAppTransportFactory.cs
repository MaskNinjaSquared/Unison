// =============================================================================
// UwpWhatsAppTransportFactory
//
// The only place that news WinRT WebSocket transports for the live stack.
// =============================================================================
using Unison.Core.Contracts;
using Unison.Uwp.Transport;

namespace Unison.Uwp.Services.Socket
{
    public sealed class UwpWhatsAppTransportFactory : IWhatsAppTransportFactory
    {
        public IWhatsAppTransport CreateStreamSocketTransport()
        {
            return new StreamSocketWebSocketTransport();
        }

        public IWhatsAppTransport CreateMessageWebSocketTransport()
        {
            return new MessageWebSocketTransport();
        }
    }
}
