// =============================================================================
// RtpStream / VideoRtpStream
//
// Send-side RTP sequencers. Audio: seq starts at 1, timestamp advances per
// packet, marker latches on the first speech frame, WARP piggyback word after
// the first packets. Video: one timestamp per access unit.
// Ports: meowcaller rtp/rtp.go (MIT)
// =============================================================================
namespace Unison.Voip.Engine.Rtp
{
    public sealed class RtpStream
    {
        private const int WarpPiggybackStartPacket = 2;

        private readonly uint _samplesPerPacket;
        private readonly bool _warpPiggyback;
        private ushort _seq;
        private uint _timestamp;
        private bool _speechStarted;
        private int _audioPacketIndex;

        public RtpStream(uint ssrc, uint samplesPerPacket, bool warpPiggyback)
        {
            Ssrc = ssrc;
            _seq = 1;
            _samplesPerPacket = samplesPerPacket;
            _warpPiggyback = warpPiggyback;
        }

        public uint Ssrc { get; set; }

        /// <summary>Next RTP header for payload, latching the marker on the first speech frame.</summary>
        public RtpHeader NextPacket(byte[] payload, bool marker)
        {
            bool dtx = RtpPacket.IsOpusDtxPayload(payload);
            bool priming = RtpPacket.IsOpusPrimingPayload(payload);
            bool speech = !dtx && !priming;
            bool useMarker = marker || (speech && !_speechStarted);
            if (speech)
            {
                _speechStarted = true;
            }

            var header = new RtpHeader
            {
                Marker = useMarker,
                PayloadType = RtpPacket.RtpPayloadTypeOpus,
                SequenceNumber = _seq,
                Timestamp = _timestamp,
                Ssrc = Ssrc,
                ExtensionWord = ResolveWarpExtension(dtx)
            };
            Advance();
            return header;
        }

        /// <summary>Pre-speech ladder packet: advances seq/timestamp, no marker or latch.</summary>
        public RtpHeader NextPreSpeechPacket()
        {
            var header = new RtpHeader
            {
                Marker = false,
                PayloadType = RtpPacket.RtpPayloadTypeOpus,
                SequenceNumber = _seq,
                Timestamp = _timestamp,
                Ssrc = Ssrc,
                ExtensionWord = ResolveWarpExtension(false)
            };
            Advance();
            return header;
        }

        private void Advance()
        {
            unchecked
            {
                _seq++;
                _timestamp += _samplesPerPacket;
            }
        }

        private uint? ResolveWarpExtension(bool dtx)
        {
            if (dtx)
            {
                return RtpPacket.WhatsappRtpExtensionDtxWord;
            }
            if (!_warpPiggyback)
            {
                return null;
            }

            int index = _audioPacketIndex;
            _audioPacketIndex++;
            return Crypto.Warp.AudioPiggybackExtensionFor(index, true, WarpPiggybackStartPacket);
        }
    }

    public sealed class VideoRtpStream
    {
        private readonly uint _ssrc;
        private ushort _seq;
        private uint _timestamp;
        private uint _lastSentTimestamp;
        private bool _sent;
        private uint _tsStride;
        private ushort _transportSequence;
        private ushort _frameNumber;
        private bool _firstPacket;

        public VideoRtpStream(uint ssrc, uint tsStride)
        {
            _ssrc = ssrc;
            _seq = 1;
            _tsStride = tsStride;
            _frameNumber = 1;
            _firstPacket = true;
        }

        public uint RtpTimestamp()
        {
            return _sent ? _lastSentTimestamp : _timestamp;
        }

        public bool SetTimestampStride(uint tsStride)
        {
            if (tsStride == 0)
            {
                return false;
            }
            _tsStride = tsStride;
            return true;
        }

        public RtpHeader NextPacket(bool lastInAccessUnit, byte mediaFrameInfo)
        {
            var header = new RtpHeader
            {
                Marker = lastInAccessUnit,
                PayloadType = RtpPacket.RtpPayloadTypeH264,
                SequenceNumber = _seq,
                Timestamp = _timestamp,
                Ssrc = _ssrc,
                VideoExtension = new VideoRtpExtension
                {
                    MediaFrameInfo = mediaFrameInfo,
                    FrameNumber = _firstPacket ? (ushort?)_frameNumber : null,
                    InitialBandwidth = 0,
                    ShortOffset = 0,
                    TransportSequence = _transportSequence
                }
            };

            _lastSentTimestamp = header.Timestamp;
            _sent = true;
            unchecked
            {
                _seq++;
                _transportSequence++;
                if (lastInAccessUnit)
                {
                    _timestamp += _tsStride;
                    _frameNumber++;
                }
            }
            _firstPacket = lastInAccessUnit;
            return header;
        }
    }
}
