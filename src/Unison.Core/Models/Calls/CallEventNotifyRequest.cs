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

        /// <summary>
        /// Outer <c>&lt;call from&gt;</c> (meowcaller <c>ev.From</c> / <c>m.from</c>) — stanza
        /// <c>to=</c> for preaccept/accept/reject/terminate. May differ from <see cref="ChatJid"/>.
        /// </summary>
        public string SignalingToJid { get; set; }

        /// <summary>Raw action child tag (offer, mute_v2, terminate, …).</summary>
        public string ActionTag { get; set; }

        /// <summary>Wire duration when present; 0 if unknown.</summary>
        public int DurationSeconds { get; set; }

        /// <summary>Engine-only action (mute_v2, relaylatency, transport): no chrome, CallLog or toast.</summary>
        public bool IsSignalingControl { get; set; }

        /// <summary>Action call-creator — the call-creator attribute on replies.</summary>
        public string CallCreatorJid { get; set; }

        /// <summary>callKey / relay carried by this stanza; null when it carried none.</summary>
        public CallMediaHints MediaHints { get; set; }
    }
}
