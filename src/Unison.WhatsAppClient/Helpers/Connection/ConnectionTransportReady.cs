// =============================================================================
// ConnectionTransportReady
//
// Shared "socket is up and Noise finished" predicates used by connect gates,
// broker transfer, and offline-replay release. Facts stay on the host.
// =============================================================================

namespace Unison.WhatsAppClient.Helpers
{
    /// <summary>Pure transport-readiness checks — no socket ownership.</summary>
    public static class ConnectionTransportReady
    {
        public static bool IsReady(bool hasSocket, bool isConnected, bool handshakeComplete) =>
            hasSocket && isConnected && handshakeComplete;

        public static bool HasHealthyRegistered(
            bool transportReady,
            bool authPresent,
            bool authRegistered) =>
            transportReady && authPresent && authRegistered;

        public static bool HasHealthyUnregistered(
            bool transportReady,
            bool authNullOrUnregistered) =>
            transportReady && authNullOrUnregistered;
    }
}
