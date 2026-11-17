namespace Unison.Voip.Abstractions
{
    /// <summary>Result of placing an outbound offer on the wire (meowcaller placeCall).</summary>
    public sealed class CallOfferWireResult
    {
        public string CallId { get; set; }

        /// <summary>Peer account JID the offer was addressed to (often LID).</summary>
        public string PeerJid { get; set; }

        /// <summary>Self LID / PN used as call-creator.</summary>
        public string CallCreatorJid { get; set; }

        /// <summary>32-byte plaintext callKey (seeds E2E-SRTP / WARP).</summary>
        public byte[] CallKey { get; set; }
    }
}
