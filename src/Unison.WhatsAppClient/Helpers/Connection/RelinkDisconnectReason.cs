// =============================================================================
// RelinkDisconnectReason
//
// Disconnect reasons that mean the companion must re-link (session invalid /
// replaced / forbidden / bad), not a soft network drop.
// =============================================================================
using Unison.Core.Models;
using Unison.Core.Helpers;

namespace Unison.WhatsAppClient.Helpers
{
    public static class RelinkDisconnectReason
    {
        public static bool Matches(DisconnectReason reason)
        {
            return reason == DisconnectReason.LoggedOut ||
                   reason == DisconnectReason.ConnectionReplaced ||
                   reason == DisconnectReason.BadSession ||
                   reason == DisconnectReason.Forbidden;
        }
    }
}
