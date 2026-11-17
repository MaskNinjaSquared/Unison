// =============================================================================
// RtpPacket
//
// RTP WARP framing: WhatsApp's 16-byte speech / 20-byte DTX headers (extension
// profile 0xdebe), Opus/MLow payload classifiers and the SRTP size estimator.
// Ports: meowcaller rtp/rtp.go (MIT)
// =============================================================================
using System;

namespace Unison.Voip.Engine.Rtp
{
    public static class RtpPacket
    {
        public const byte RtpPayloadTypeOpus = 120;

        /// <summary>Carries protobuf call reactions and other RTC app data.</summary>
        public const byte RtpPayloadTypeAppData = 119;

        /// <summary>WhatsApp video (H.264) payload type, used to demux video off the relay.</summary>
        public const byte RtpPayloadTypeH264 = 97;

        public const ushort WhatsappRtpExtensionProfile = 0xdebe;
        public const int WhatsappRtpHeaderSize = 16;
        public const int WhatsappRtpHeaderDtxSize = 20;
        public const int WhatsappVideoRtpHeaderSize = 28;
        public const uint WhatsappRtpExtensionDtxWord = 0x30010000;
        public const byte VideoMediaFrameInfoIDR = 0x08;
        public const byte VideoMediaFrameInfoDelta = 0x20;

        private const int RtpVersion = 2;
        private const int RtpFixedHeaderSize = 12;
        private const int SrtpAuthTagLen = 10;
        private const int SrtpAuthTagLenShort = 4;

        private static readonly byte[] PrimingFrame1 =
        {
            0x12, 0x36, 0x26, 0x2b, 0x4a, 0xc8, 0x2b, 0x09, 0xc9, 0x1f, 0x34, 0xc2, 0xd6, 0x7a, 0x01, 0x73,
            0x1b, 0x2e
        };

        private static readonly byte[] PrimingFrame1Wasm =
        {
            0x32, 0x36, 0x26, 0x2b, 0x4a, 0xcb, 0x1b, 0x5f, 0xba, 0x91, 0x68, 0x7e, 0xb8, 0x50, 0x93, 0x58,
            0xe6, 0xd0, 0xa3, 0xa9, 0xd7, 0x1d, 0x81, 0x8c
        };

        private static readonly byte[] PrimingFrame2 = { 0x90, 0xb8, 0x14, 0x14, 0xc4 };

        /// <summary>Android first priming frame (18 bytes, fresh copy).</summary>
        public static byte[] OpusPrimingFrame1
        {
            get { return (byte[])PrimingFrame1.Clone(); }
        }

        /// <summary>WASM/Web caller priming frame (24 bytes, fresh copy).</summary>
        public static byte[] OpusPrimingFrame1Wasm
        {
            get { return (byte[])PrimingFrame1Wasm.Clone(); }
        }

        /// <summary>Second priming frame (5 bytes, fresh copy).</summary>
        public static byte[] OpusPrimingFrame2
        {
            get { return (byte[])PrimingFrame2.Clone(); }
        }

        public static bool IsWhatsappOpusRtpPayload(byte payloadType)
        {
            return payloadType == RtpPayloadTypeOpus || payloadType == 121;
        }

        /// <summary>DTX / comfort-noise frames (RFC 0x10, mlow 0x90, warmup).</summary>
        public static bool IsOpusDtxPayload(byte[] payload)
        {
            int length = payload == null ? 0 : payload.Length;
            if (length == 0)
            {
                return false;
            }
            if (length == 1)
            {
                return payload[0] == 0x10 || payload[0] == 0x88 || payload[0] == 0x90;
            }
            if (length > 15)
            {
                return false;
            }

            byte b0 = payload[0];
            if ((b0 & 0xf8) == 0x08 || b0 == 0x0a)
            {
                return true;
            }
            return (b0 & 0xf0) == 0x30 && length <= 6;
        }

        /// <summary>MLow speech frames (20 ms 0x48..0x4f or 60 ms 0x50..0x57).</summary>
        public static bool IsOpusMlowSpeechPayload(byte[] payload)
        {
            if (payload == null || payload.Length < 18)
            {
                return false;
            }
            byte b0 = payload[0];
            return (b0 & 0xf8) == 0x48 || (b0 & 0xf8) == 0x50;
        }

        public static bool IsOpusPrimingPayload(byte[] payload)
        {
            return BytesEqual(payload, PrimingFrame1) || BytesEqual(payload, PrimingFrame2);
        }

        /// <summary>On-wire SRTP size estimate: header + opus + auth tag.</summary>
        public static int EstimateSrtpRtpWireBytes(byte[] opusPayload)
        {
            int payloadLength = opusPayload == null ? 0 : opusPayload.Length;
            bool dtx = IsOpusDtxPayload(opusPayload);
            int headerSize = dtx ? WhatsappRtpHeaderDtxSize : WhatsappRtpHeaderSize;
            bool shortTag = (dtx && headerSize == WhatsappRtpHeaderDtxSize) ||
                (!dtx && headerSize == WhatsappRtpHeaderSize &&
                    (IsOpusPrimingPayload(opusPayload) || payloadLength <= 18));
            int tagLen = shortTag ? SrtpAuthTagLenShort : SrtpAuthTagLen;
            return headerSize + payloadLength + tagLen;
        }

        /// <summary>Full on-wire header size (12 + CSRC + extension); false if malformed.</summary>
        public static bool TryGetRtpHeaderByteLength(byte[] data, out int headerLength)
        {
            headerLength = 0;
            if (data == null || data.Length < RtpFixedHeaderSize)
            {
                return false;
            }
            if (((data[0] >> 6) & 0x03) != RtpVersion)
            {
                return false;
            }

            int cc = data[0] & 0x0f;
            int length = RtpFixedHeaderSize + cc * 4;
            if (data.Length < length)
            {
                return false;
            }

            bool hasExtension = ((data[0] >> 4) & 1) == 1;
            if (hasExtension)
            {
                if (data.Length < length + 4)
                {
                    return false;
                }
                int extWords = (data[length + 2] << 8) | data[length + 3];
                length += 4 + extWords * 4;
                if (data.Length < length)
                {
                    return false;
                }
            }

            headerLength = length;
            return true;
        }

        /// <summary>Extension profile and its word-aligned payload (copied out).</summary>
        public static bool TryGetRtpExtensionProfileAndData(byte[] data, out ushort profile, out byte[] extensionData)
        {
            profile = 0;
            extensionData = null;
            if (data == null || data.Length < RtpFixedHeaderSize || ((data[0] >> 6) & 0x03) != RtpVersion)
            {
                return false;
            }

            int extensionOffset = RtpFixedHeaderSize + (data[0] & 0x0f) * 4;
            if (data.Length < extensionOffset || (data[0] & 0x10) == 0)
            {
                return false;
            }
            if (data.Length < extensionOffset + 4)
            {
                return false;
            }

            int words = ReadUInt16(data, extensionOffset + 2);
            int dataStart = extensionOffset + 4;
            int dataEnd = dataStart + words * 4;
            if (dataEnd > data.Length)
            {
                return false;
            }

            profile = ReadUInt16(data, extensionOffset);
            extensionData = new byte[dataEnd - dataStart];
            Buffer.BlockCopy(data, dataStart, extensionData, 0, extensionData.Length);
            return true;
        }

        /// <summary>Decodes WhatsApp's one-byte-header video extensions.</summary>
        public static bool TryParseWhatsappVideoExtension(byte[] data, out VideoRtpExtension extension)
        {
            extension = null;
            ushort profile;
            byte[] ext;
            if (!TryGetRtpExtensionProfileAndData(data, out profile, out ext) || profile != WhatsappRtpExtensionProfile)
            {
                return false;
            }

            var parsed = new VideoRtpExtension();
            bool hasFrameInfo = false;
            bool hasShortOffset = false;
            int offset = 0;
            while (offset < ext.Length)
            {
                byte header = ext[offset];
                offset++;
                if (header == 0)
                {
                    continue;
                }

                int id = header >> 4;
                int length = (header & 0x0f) + 1;
                if (id == 15 || offset + length > ext.Length)
                {
                    return false;
                }

                int valueOffset = offset;
                offset += length;
                switch (id)
                {
                    case 3:
                        if (length != 1 && length != 3)
                        {
                            return false;
                        }
                        parsed.MediaFrameInfo = ext[valueOffset];
                        if (length == 3)
                        {
                            parsed.FrameNumber = ReadUInt16(ext, valueOffset + 1);
                        }
                        hasFrameInfo = true;
                        break;
                    case 5:
                        if (length != 2)
                        {
                            return false;
                        }
                        parsed.InitialBandwidth = ReadUInt16(ext, valueOffset);
                        break;
                    case 6:
                        if (length != 2)
                        {
                            return false;
                        }
                        parsed.ShortOffset = unchecked((short)ReadUInt16(ext, valueOffset));
                        hasShortOffset = true;
                        break;
                    case 9:
                        if (length != 2)
                        {
                            return false;
                        }
                        parsed.TransportSequence = ReadUInt16(ext, valueOffset);
                        break;
                }
            }

            if (!hasFrameInfo || !hasShortOffset)
            {
                return false;
            }
            extension = parsed;
            return true;
        }

        public static bool IsRtpVersion2(byte[] data)
        {
            return data != null && data.Length >= RtpFixedHeaderSize && ((data[0] >> 6) & 0x03) == RtpVersion;
        }

        /// <summary>
        /// Parses the fixed header fields and any video extension; the audio extension
        /// word is not decoded.
        /// </summary>
        public static bool TryParseRtpHeader(byte[] data, out RtpHeader header)
        {
            header = null;
            int headerLength;
            if (!TryGetRtpHeaderByteLength(data, out headerLength))
            {
                return false;
            }

            VideoRtpExtension videoExtension;
            TryParseWhatsappVideoExtension(data, out videoExtension);
            header = new RtpHeader
            {
                Marker = ((data[1] >> 7) & 1) == 1,
                PayloadType = (byte)(data[1] & 0x7f),
                SequenceNumber = ReadUInt16(data, 2),
                Timestamp = ReadUInt32(data, 4),
                Ssrc = ReadUInt32(data, 8),
                ExtensionWord = null,
                VideoExtension = videoExtension
            };
            return true;
        }

        /// <summary>Encodes the RTP header and its WhatsApp extension.</summary>
        public static byte[] EncodeRtpHeader(RtpHeader header)
        {
            if (header == null)
            {
                throw new ArgumentNullException(nameof(header));
            }

            byte[] videoExtension = header.VideoExtension != null ? header.VideoExtension.Encode() : null;
            int size = videoExtension != null
                ? WhatsappRtpHeaderSize + videoExtension.Length
                : header.ByteSize();
            var buf = new byte[size];
            buf[0] = RtpVersion << 6;
            if (size > RtpFixedHeaderSize)
            {
                buf[0] |= 0x10;
            }
            buf[1] = (byte)(header.PayloadType & 0x7f);
            if (header.Marker)
            {
                buf[1] |= 0x80;
            }
            WriteUInt16(buf, 2, header.SequenceNumber);
            WriteUInt32(buf, 4, header.Timestamp);
            WriteUInt32(buf, 8, header.Ssrc);

            if (size >= 16)
            {
                WriteUInt16(buf, 12, WhatsappRtpExtensionProfile);
                ushort extWords = 0;
                if (videoExtension != null)
                {
                    extWords = (ushort)(videoExtension.Length / 4);
                }
                else if (header.ExtensionWord.HasValue)
                {
                    extWords = 1;
                }
                WriteUInt16(buf, 14, extWords);
            }

            if (videoExtension != null)
            {
                Buffer.BlockCopy(videoExtension, 0, buf, 16, videoExtension.Length);
            }
            else if (size >= 20 && header.ExtensionWord.HasValue)
            {
                WriteUInt32(buf, 16, header.ExtensionWord.Value);
            }
            return buf;
        }

        internal static ushort ReadUInt16(byte[] data, int offset)
        {
            return (ushort)((data[offset] << 8) | data[offset + 1]);
        }

        internal static uint ReadUInt32(byte[] data, int offset)
        {
            return ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) |
                ((uint)data[offset + 2] << 8) | data[offset + 3];
        }

        internal static void WriteUInt16(byte[] buffer, int offset, ushort value)
        {
            buffer[offset] = (byte)(value >> 8);
            buffer[offset + 1] = (byte)value;
        }

        internal static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            int aLength = a == null ? 0 : a.Length;
            int bLength = b == null ? 0 : b.Length;
            if (aLength != bLength)
            {
                return false;
            }
            for (int i = 0; i < aLength; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }
            return true;
        }
    }
}
