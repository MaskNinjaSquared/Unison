// =============================================================================
// RelayPacketClassifier
//
// First-byte demux of packets on the WhatsApp relay DataChannel: STUN vs
// RTP/RTCP. The DTLS/SCTP/DataChannel transport itself is not ported here.
// =============================================================================
// Ports: meowcaller relay/relay.go (MIT)
namespace Unison.Voip.Engine.Relay
{
    public enum RelayPacketKind
    {
        Stun = 0,
        Rtcp = 1,
        Rtp = 2,
        Other = 3
    }

    public static class RelayPacketClassifier
    {
        /// <summary>Pre-negotiated (id=0) DataChannel label WA Web uses.</summary>
        public const string DataChannelLabel = "pre-negotiated";

        /// <summary>SCTP-over-DTLS WebRTC port (a WebRTC convention, not negotiated config).</summary>
        public const int SctpPort = 5000;

        /// <summary>
        /// Demuxes STUN from RTP/RTCP using the RTP version and payload type ranges.
        /// WhatsApp video RTCP sets a profile bit, so its first byte can be 0x91.
        /// </summary>
        public static RelayPacketKind ClassifyRelayPacket(byte[] data)
        {
            if (data == null || data.Length < 2)
            {
                return RelayPacketKind.Other;
            }
            byte first = data[0];
            if ((first & 0xc0) == 0)
            {
                return RelayPacketKind.Stun;
            }
            if (data[1] >= 192 && data[1] <= 223)
            {
                return RelayPacketKind.Rtcp;
            }
            if (first >> 6 == 2)
            {
                return RelayPacketKind.Rtp;
            }
            return RelayPacketKind.Other;
        }
    }
}
