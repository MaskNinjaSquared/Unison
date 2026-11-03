// =============================================================================
// CallEventNotifyRequest
//
// Host → MessageFacade: turn a finished CallHandler status into chat chrome.
// =============================================================================
using System;

namespace Unison.Core.Models
{
    public enum CallEventChromeKind
    {
        Missed = 0,
        Completed = 1,
        /// <summary>Live offer / offer_notice — toast only, no CallLog / chrome.</summary>
        Incoming = 2,
        /// <summary>Incoming Accept/Terminate on a companion that cannot answer locally.</summary>
        AnsweredElsewhere = 3
    }

    public sealed class CallEventNotifyRequest
    {
        public string ChatJid { get; set; }

        public string CallId { get; set; }

        public CallEventChromeKind Kind { get; set; }

        public bool IsVideo { get; set; }

        public DateTime TimestampUtc { get; set; }

        /// <summary>CallOffer.From — who initiated; used for Outgoing vs Incoming.</summary>
        public string CallerJid { get; set; }

        /// <summary>Wire duration when present; 0 if unknown.</summary>
        public int DurationSeconds { get; set; }
    }
}
