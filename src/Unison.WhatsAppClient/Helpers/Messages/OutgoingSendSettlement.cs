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
using Unison.Core.Helpers;

namespace Unison.WhatsAppClient.Helpers
{
    public static class OutgoingSendSettlement
    {
        public static bool ShouldPromotePending(string currentStatus)
        {
            return string.Equals(currentStatus, ChatMessage.StatusPending, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Whether the bubble may move to sent after the transport accepted the payload.
        /// Honours <see cref="ConnectionStabilityExperiment"/> for the failed→sent race.
        /// </summary>
        public static bool ShouldPromoteAfterTransportAccept(string currentStatus)
        {
            return ConnectionStabilityExperiment.ShouldPromoteBubbleAfterAccept(currentStatus);
        }
    }
}
