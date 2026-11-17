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

        /// <summary>Call length in seconds when the stanza carries it; otherwise 0.</summary>
        public int DurationSeconds { get; set; }

        /// <summary>Child tag of the action node (offer, reject, terminate, …).</summary>
        public string ActionTag { get; set; }

        /// <summary>True when the action carried an <c>enc</c> child (opaque to Socket; for Voip).</summary>
        public bool HasEncryptedPayload { get; set; }

        /// <summary>Count of action children (opaque payload presence without copying bytes yet).</summary>
        public int OpaqueChildCount { get; set; }

        /// <summary>The action's call-creator attribute (stanza call-creator for replies).</summary>
        public string CallCreator { get; set; }

        /// <summary>Decrypted media root key from a live 1:1 offer; null otherwise. Never log it.</summary>
        public byte[] CallKey { get; set; }

        /// <summary>&lt;relay&gt; under the action (offer / relaylatency / transport), when present.</summary>
        public CallRelayInfo Relay { get; set; }

        /// <summary>Caller relaylatency candidates the callee must echo back.</summary>
        public System.Collections.Generic.List<CallRelayLatencyProbe> RelayLatencyProbes { get; set; }
    }
}
