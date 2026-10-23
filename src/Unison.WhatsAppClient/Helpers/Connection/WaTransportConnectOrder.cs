// =============================================================================
// WaTransportConnectOrder
//
// Whether connect tries StreamSocket first or MessageWebSocket only. Kept pure
// so SocketBridge does not embed the Mobile / experiment branching inline.
// =============================================================================

namespace Unison.WhatsAppClient.Helpers
{
    /// <summary>Primary vs fallback transport choice for a connect attempt.</summary>
    public static class WaTransportConnectOrder
    {
        /// <summary>
        /// Desktop always prefers StreamSocket. Mobile prefers MessageWebSocket unless
        /// the stability experiment asks for StreamSocket-first (with classic fallback).
        /// </summary>
        public static bool PreferStreamSocketFirst(bool isMobile, bool preferStreamOnMobile)
        {
            return !isMobile || preferStreamOnMobile;
        }
    }
}
