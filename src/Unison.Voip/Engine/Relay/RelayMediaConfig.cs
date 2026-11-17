// =============================================================================
// RelayMediaConfig
//
// Portable media inputs for the relay media loop. Mapped from Core
// CallMediaHints by the host — Voip does not reference Core.
// =============================================================================
using System.Collections.Generic;

namespace Unison.Voip.Engine.Relay
{
    public sealed class RelayMediaConfig
    {
        public string CallId { get; set; }

        /// <summary>Self device-qualified LID (or FormatE2ESrtpParticipantID input).</summary>
        public string SelfLid { get; set; }

        /// <summary>Peer LID whose keys decrypt inbound RTP (relay peer when known).</summary>
        public string PeerLid { get; set; }

        public bool Inbound { get; set; }

        /// <summary>32-byte E2E call key. Never log.</summary>
        public byte[] CallKey { get; set; }

        public byte[] RelayKey { get; set; }

        public List<byte[]> RelayTokens { get; set; } = new List<byte[]>();

        public List<RelayMediaEndpoint> Endpoints { get; set; } = new List<RelayMediaEndpoint>();
    }

    public sealed class RelayMediaEndpoint
    {
        public string RelayName { get; set; }

        public uint TokenId { get; set; }

        public uint AuthTokenId { get; set; }

        public bool IsFna { get; set; }

        public string Ipv4 { get; set; }

        public int Port { get; set; }
    }
}
