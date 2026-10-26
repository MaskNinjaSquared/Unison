// =============================================================================
// IQrCodeWireHost
//
// Side effects for QrCodeWireDispatch (OnQRCodeReceived).
// =============================================================================
namespace Unison.Core.Contracts.WhatsApp
{
    public interface IQrCodeWireHost
    {
        void LogStaleQrIgnored();

        void LogQrRaising(int length);

        void MarkQrDelivered();

        void RaiseQrCodeReceived(string qr);
    }
}
