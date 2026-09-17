// =============================================================================
// IncomingSelfChatCollapseGate
//
// After live DM routing lands on self-chat, a distinct peerRecipientLid is a
// transient bucket that should merge into the canonical conversation.
// =============================================================================
using System;

namespace Unison.Core.Helpers
{
    public static class IncomingSelfChatCollapseGate
    {
        public static bool ShouldCollapse(
            string routingReason,
            string normalizedPeerRecipientLid,
            string resolvedChatJid)
        {
            if (!string.Equals(routingReason, "self-chat", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(normalizedPeerRecipientLid))
            {
                return false;
            }

            return !string.Equals(
                normalizedPeerRecipientLid,
                resolvedChatJid,
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
