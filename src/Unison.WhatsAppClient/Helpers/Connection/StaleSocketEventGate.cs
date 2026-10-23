// =============================================================================
// StaleSocketEventGate
//
// Stream/error events from a socket we already replaced are ignored — unless
// the code is an explicit logout, which still revokes the account even when
// the reconnect loop made the sender "stale".
// =============================================================================

namespace Unison.WhatsAppClient.Helpers
{
    public static class StaleSocketEventGate
    {
        /// <summary>
        /// True when the host should drop the event without latching or reporting.
        /// </summary>
        public static bool ShouldIgnore(bool isCurrentSocket, bool isExplicitLogout)
        {
            return !isCurrentSocket && !isExplicitLogout;
        }
    }
}
