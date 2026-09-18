// =============================================================================
// RelinkDisconnectReason
//
// Disconnect reasons that mean the companion must re-link (session invalid /
// replaced / forbidden / bad), not a soft network drop.
// =============================================================================
namespace Unison.Core.Helpers
{
    using Unison.Core.Models;

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
