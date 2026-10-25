// =============================================================================
// QrCodeWireOwner
//
// Owns IQrCodeWireHost. QR event raise / delivered flag stay as ctor ports.
// =============================================================================
using System;
using Unison.Core.Contracts.WhatsApp;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class QrCodeWireOwner : IQrCodeWireHost
    {
        private readonly Action _logStale;
        private readonly Action<int> _logRaising;
        private readonly Action _markDelivered;
        private readonly Action<string> _raiseQr;

        public QrCodeWireOwner(
            Action logStale,
            Action<int> logRaising,
            Action markDelivered,
            Action<string> raiseQr)
        {
            _logStale = logStale ?? throw new ArgumentNullException(nameof(logStale));
            _logRaising = logRaising ?? throw new ArgumentNullException(nameof(logRaising));
            _markDelivered = markDelivered
                ?? throw new ArgumentNullException(nameof(markDelivered));
            _raiseQr = raiseQr ?? throw new ArgumentNullException(nameof(raiseQr));
        }

        public void Handle(string qr, bool isCurrentSocket) =>
            QrCodeWireDispatch.Handle(this, qr, isCurrentSocket);

        void IQrCodeWireHost.LogStaleQrIgnored() => _logStale();

        void IQrCodeWireHost.LogQrRaising(int length) => _logRaising(length);

        void IQrCodeWireHost.MarkQrDelivered() => _markDelivered();

        void IQrCodeWireHost.RaiseQrCodeReceived(string qr) => _raiseQr(qr);
    }
}
