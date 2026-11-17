// =============================================================================
// CallMediaHints
//
// Protocol-neutral copy of what a call stanza carried for the media engine:
// the decrypted callKey and the relay the call is bridged through. Host maps
// it from Socket CallOffer; CallFacade keeps it for the active call only.
// Holds key material — never log the byte arrays.
// =============================================================================
using System.Collections.Generic;

namespace Unison.Core.Models
{
    public sealed class CallMediaHints
    {
        public byte[] CallKey { get; set; }

        public byte[] RelayKey { get; set; }

        /// <summary>Indexed by relay token id; gaps are null.</summary>
        public List<byte[]> RelayTokens { get; set; } = new List<byte[]>();

        public List<CallMediaRelayEndpoint> RelayEndpoints { get; set; } = new List<CallMediaRelayEndpoint>();

        public string RelayPeerJid { get; set; }

        public List<CallRelayLatencyCandidate> LatencyProbes { get; set; } = new List<CallRelayLatencyCandidate>();
    }

    public sealed class CallMediaRelayEndpoint
    {
        public uint RelayId { get; set; }

        public string RelayName { get; set; }

        public uint TokenId { get; set; }

        public uint AuthTokenId { get; set; }

        public bool IsFna { get; set; }

        public string Ipv4 { get; set; }

        public int Port { get; set; }
    }

    public sealed class CallRelayLatencyCandidate
    {
        public uint LatencyMs { get; set; }

        public string RelayName { get; set; }

        public byte[] AddressBytes { get; set; }
    }
}
