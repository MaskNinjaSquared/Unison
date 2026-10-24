// =============================================================================
// ConnectionStatusNotifyPolicy
//
// When a connection status should clear the "reconnect required" toast. The
// presenter call stays on the host.
// =============================================================================
using System;

namespace Unison.WhatsAppClient.Helpers
{
    public static class ConnectionStatusNotifyPolicy
    {
        public static bool ShouldClearReconnectRequiredToast(string status) =>
            string.Equals(status, "connected", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "synced", StringComparison.OrdinalIgnoreCase);
    }
}
