// =============================================================================
// QrCodeWireDispatch
//
// OnQRCodeReceived body: stale ignore, mark delivered, raise. Handler wiring
// stays on the host.
// =============================================================================
using System;
using Unison.Core.Contracts.WhatsApp;

namespace Unison.WhatsAppClient.Helpers
{
    public static class QrCodeWireDispatch
    {
        public static void Handle(IQrCodeWireHost host, string qr, bool isCurrentSocket)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (!isCurrentSocket)
            {
                host.LogStaleQrIgnored();
                return;
            }

            host.LogQrRaising(qr?.Length ?? 0);
            host.MarkQrDelivered();
            host.RaiseQrCodeReceived(qr);
        }
    }
}
