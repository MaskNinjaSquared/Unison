// =============================================================================
// CallRelayInfo
//
// The media relay a call is bridged through, as the server announces it under
// <relay> (offer / relaylatency / transport). The engine needs all of it: the
// <key> keys STUN MESSAGE-INTEGRITY, the token at an endpoint's token_id
// authorises the allocate, and the te2 endpoint is where the DataChannel goes.
//
// Ports: meowcaller engine.go relayData / relayEndpoint / rlProbe
// =============================================================================
using System.Collections.Generic;

namespace Unison.Socket.Models
{
    public sealed class CallRelayInfo
    {
        /// <summary>Raw &lt;key&gt; content — the STUN MESSAGE-INTEGRITY key.</summary>
        public byte[] Key { get; set; }

        /// <summary>Indexed by &lt;token id&gt;; gaps stay null.</summary>
        public List<byte[]> Tokens { get; } = new List<byte[]>();

        /// <summary>IPv4 te2 endpoints only (IPv6 is skipped like meowcaller).</summary>
        public List<CallRelayEndpoint> Endpoints { get; } = new List<CallRelayEndpoint>();

        /// <summary>Relay-elected peer device (participant whose pid = peer_pid), when stated.</summary>
        public string PeerJid { get; set; }
    }

    public sealed class CallRelayEndpoint
    {
        public uint RelayId { get; set; }

        public string RelayName { get; set; }

        public uint TokenId { get; set; }

        public uint AuthTokenId { get; set; }

        /// <summary>The caller's uplink relay; an inbound call must allocate on it.</summary>
        public bool IsFna { get; set; }

        public string Ipv4 { get; set; }

        public int Port { get; set; }
    }

    /// <summary>One &lt;te&gt; candidate of a relaylatency probe the callee echoes back.</summary>
    public sealed class CallRelayLatencyProbe
    {
        public uint LatencyMs { get; set; }

        public string RelayName { get; set; }

        public byte[] AddressBytes { get; set; }
    }
}
