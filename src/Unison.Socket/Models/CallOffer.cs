// =============================================================================
// CallOffer
//
// An incoming or ending call.
//
// The same type covers the whole life of a call because the server does: the
// offer, the ringing, and whichever of accept, reject, timeout or terminate ends
// it all arrive as <call> stanzas differing only in their child tag.
//
// Ports: rc14 WACallEvent in src/Types/Call.ts
// =============================================================================
namespace Unison.Socket.Models
{
    public enum CallStatus
    {
        Offer = 0,
        Ringing = 1,
        Timeout = 2,
        Reject = 3,
        Accept = 4,
        Terminate = 5
    }

    public sealed class CallOffer
    {
        /// <summary>The call id, which is what an accept or reject has to quote back.</summary>
        public string Id { get; set; }

        /// <summary>Who is calling.</summary>
        public string From { get; set; }

        /// <summary>Where it belongs in the UI: the group for a group call, otherwise the caller.</summary>
        public string ChatId { get; set; }

        public string GroupJid { get; set; }

        public bool IsGroup { get; set; }

        public bool IsVideo { get; set; }

        public CallStatus Status { get; set; }

        /// <summary>Unix seconds as reported by the server.</summary>
        public long Date { get; set; }

        /// <summary>True when this arrived in the offline backlog, so it is history, not a ringing phone.</summary>
        public bool Offline { get; set; }
    }
}
