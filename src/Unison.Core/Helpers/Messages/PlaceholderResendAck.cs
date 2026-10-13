// =============================================================================
// PlaceholderResendAck
//
// Whether an ack node is about a message resend, and whether the phone refused it.
// =============================================================================
using System;

namespace Unison.Core.Helpers
{
    public static class PlaceholderResendAck
    {
        public static bool TryRead(string ackClass, string ackId, string ackError, out bool rejected)
        {
            rejected = false;
            if (!string.Equals(ackClass, "message", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(ackId))
            {
                return false;
            }

            rejected = !string.IsNullOrWhiteSpace(ackError);
            return true;
        }
    }
}
