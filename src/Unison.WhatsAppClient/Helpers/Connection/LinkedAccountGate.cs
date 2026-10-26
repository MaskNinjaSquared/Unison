// =============================================================================
// LinkedAccountGate
//
// Whether persisted auth counts as a linked companion account (toast suppress /
// IsRegistered). Auth load / Me object stay on the host.
// =============================================================================

namespace Unison.WhatsAppClient.Helpers
{
    public static class LinkedAccountGate
    {
        /// <summary>
        /// Registered Me with a non-blank id — suppress "Unison desconectado" toasts.
        /// </summary>
        public static bool HasLinkedAccount(bool registered, bool mePresent, string meId) =>
            registered &&
            mePresent &&
            !string.IsNullOrWhiteSpace(meId);

        /// <summary>
        /// Same shape as <c>IsRegisteredAsync</c>: auth loaded, registered, Me present.
        /// </summary>
        public static bool IsRegisteredWithIdentity(
            bool authPresent,
            bool registered,
            bool mePresent) =>
            authPresent && registered && mePresent;
    }
}
