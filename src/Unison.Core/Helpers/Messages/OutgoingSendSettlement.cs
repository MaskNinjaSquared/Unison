// =============================================================================
// OutgoingSendSettlement
//
// What to do with a bubble's status once the transport has accepted the text.
//
// A receipt can land before SendTextMessageAsync returns. Overwriting that with
// "sent" would walk the ticks backwards. Only a bubble still sitting on pending
// is promoted.
// =============================================================================
using System;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public static class OutgoingSendSettlement
    {
        public static bool ShouldPromotePending(string currentStatus)
        {
            return string.Equals(currentStatus, ChatMessage.StatusPending, StringComparison.OrdinalIgnoreCase);
        }
    }
}
