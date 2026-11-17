// =============================================================================
// RtpHeader
//
// Fixed RTP header plus WhatsApp's optional 0xdebe extension: either one audio
// extension word (DTX / WARP piggyback) or the video metadata block.
// Ports: meowcaller rtp/rtp.go (MIT)
// =============================================================================
using System.Collections.Generic;

namespace Unison.Voip.Engine.Rtp
{
    public sealed class RtpHeader
    {
        public bool Marker { get; set; }

        public byte PayloadType { get; set; }

        public ushort SequenceNumber { get; set; }

        public uint Timestamp { get; set; }

        public uint Ssrc { get; set; }

        /// <summary>Null = no audio extension word.</summary>
        public uint? ExtensionWord { get; set; }

        /// <summary>Null = no video extension block.</summary>
        public VideoRtpExtension VideoExtension { get; set; }

        /// <summary>On-wire header size.</summary>
        public int ByteSize()
        {
            if (VideoExtension != null)
            {
                return RtpPacket.WhatsappRtpHeaderSize + VideoExtension.Encode().Length;
            }
            if (ExtensionWord.HasValue)
            {
                return RtpPacket.WhatsappRtpHeaderDtxSize;
            }
            return RtpPacket.WhatsappRtpHeaderSize;
        }
    }

    /// <summary>WhatsApp's video RTP metadata extension set.</summary>
    public sealed class VideoRtpExtension
    {
        public byte MediaFrameInfo { get; set; }

        public ushort? FrameNumber { get; set; }

        public ushort InitialBandwidth { get; set; }

        public short ShortOffset { get; set; }

        public ushort TransportSequence { get; set; }

        /// <summary>CVO receiver rotation as clockwise quarter turns.</summary>
        public int DisplayOrientation()
        {
            return MediaFrameInfo & 0x03;
        }

        internal byte[] Encode()
        {
            int frameInfoLength = FrameNumber.HasValue ? 3 : 1;
            var ext = new List<byte>(16);
            ext.Add((byte)(0x30 | (frameInfoLength - 1)));
            ext.Add(MediaFrameInfo);
            if (FrameNumber.HasValue)
            {
                AddUInt16(ext, FrameNumber.Value);
            }
            ext.Add(0x51);
            AddUInt16(ext, InitialBandwidth);
            ext.Add(0x61);
            AddUInt16(ext, unchecked((ushort)ShortOffset));
            ext.Add(0x91);
            AddUInt16(ext, TransportSequence);
            while (ext.Count % 4 != 0)
            {
                ext.Add(0);
            }
            return ext.ToArray();
        }

        private static void AddUInt16(List<byte> buffer, ushort value)
        {
            buffer.Add((byte)(value >> 8));
            buffer.Add((byte)value);
        }
    }
}
