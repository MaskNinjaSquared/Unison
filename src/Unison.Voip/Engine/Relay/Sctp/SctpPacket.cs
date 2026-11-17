// =============================================================================
// SctpPacket
//
// Minimal SCTP packet encode/decode for a WebRTC client association.
// Ports the wire shapes from RFC 4960 used by pion/sctp.
// =============================================================================
using System;
using System.Collections.Generic;

namespace Unison.Voip.Engine.Relay.Sctp
{
    public sealed class SctpChunk
    {
        public byte Type { get; set; }

        public byte Flags { get; set; }

        public byte[] Value { get; set; }
    }

    public static class SctpPacket
    {
        public const ushort DefaultPort = 5000;

        public static byte[] EncodeInit(
            ushort sourcePort,
            ushort destPort,
            uint initiateTag,
            uint aRwnd,
            ushort outboundStreams,
            ushort inboundStreams,
            uint initialTsn)
        {
            var value = new byte[16];
            WriteUInt32(value, 0, initiateTag);
            WriteUInt32(value, 4, aRwnd);
            WriteUInt16(value, 8, outboundStreams);
            WriteUInt16(value, 10, inboundStreams);
            WriteUInt32(value, 12, initialTsn);
            return Encode(sourcePort, destPort, verificationTag: 0, new SctpChunk
            {
                Type = SctpChunkType.Init,
                Flags = 0,
                Value = value
            });
        }

        public static byte[] EncodeInitAck(
            ushort sourcePort,
            ushort destPort,
            uint verificationTag,
            uint initiateTag,
            uint aRwnd,
            ushort outboundStreams,
            ushort inboundStreams,
            uint initialTsn,
            byte[] stateCookie)
        {
            if (stateCookie == null)
            {
                throw new ArgumentNullException(nameof(stateCookie));
            }

            var value = new byte[16 + 4 + stateCookie.Length];
            WriteUInt32(value, 0, initiateTag);
            WriteUInt32(value, 4, aRwnd);
            WriteUInt16(value, 8, outboundStreams);
            WriteUInt16(value, 10, inboundStreams);
            WriteUInt32(value, 12, initialTsn);
            // State cookie parameter type=7
            WriteUInt16(value, 16, 7);
            WriteUInt16(value, 18, (ushort)(4 + stateCookie.Length));
            Buffer.BlockCopy(stateCookie, 0, value, 20, stateCookie.Length);
            return Encode(sourcePort, destPort, verificationTag, new SctpChunk
            {
                Type = SctpChunkType.InitAck,
                Flags = 0,
                Value = value
            });
        }

        public static byte[] EncodeCookieEcho(
            ushort sourcePort,
            ushort destPort,
            uint verificationTag,
            byte[] cookie)
        {
            return Encode(sourcePort, destPort, verificationTag, new SctpChunk
            {
                Type = SctpChunkType.CookieEcho,
                Flags = 0,
                Value = cookie ?? new byte[0]
            });
        }

        public static byte[] EncodeCookieAck(ushort sourcePort, ushort destPort, uint verificationTag)
        {
            return Encode(sourcePort, destPort, verificationTag, new SctpChunk
            {
                Type = SctpChunkType.CookieAck,
                Flags = 0,
                Value = new byte[0]
            });
        }

        public static byte[] EncodeData(
            ushort sourcePort,
            ushort destPort,
            uint verificationTag,
            uint tsn,
            ushort streamId,
            ushort ssn,
            uint ppid,
            byte[] payload,
            bool beginning,
            bool ending)
        {
            payload = payload ?? new byte[0];
            var value = new byte[12 + payload.Length];
            WriteUInt32(value, 0, tsn);
            WriteUInt16(value, 4, streamId);
            WriteUInt16(value, 6, ssn);
            WriteUInt32(value, 8, ppid);
            Buffer.BlockCopy(payload, 0, value, 12, payload.Length);
            byte flags = 0;
            if (beginning)
            {
                flags |= 0x02;
            }

            if (ending)
            {
                flags |= 0x01;
            }

            return Encode(sourcePort, destPort, verificationTag, new SctpChunk
            {
                Type = SctpChunkType.Data,
                Flags = flags,
                Value = value
            });
        }

        public static byte[] EncodeSack(
            ushort sourcePort,
            ushort destPort,
            uint verificationTag,
            uint cumulativeTsnAck,
            uint aRwnd)
        {
            var value = new byte[12];
            WriteUInt32(value, 0, cumulativeTsnAck);
            WriteUInt32(value, 4, aRwnd);
            WriteUInt16(value, 8, 0); // gap blocks
            WriteUInt16(value, 10, 0); // duplicate TSNs
            return Encode(sourcePort, destPort, verificationTag, new SctpChunk
            {
                Type = SctpChunkType.Sack,
                Flags = 0,
                Value = value
            });
        }

        public static byte[] EncodeHeartbeatAck(
            ushort sourcePort,
            ushort destPort,
            uint verificationTag,
            byte[] info)
        {
            return Encode(sourcePort, destPort, verificationTag, new SctpChunk
            {
                Type = SctpChunkType.HeartbeatAck,
                Flags = 0,
                Value = info ?? new byte[0]
            });
        }

        public static byte[] Encode(
            ushort sourcePort,
            ushort destPort,
            uint verificationTag,
            params SctpChunk[] chunks)
        {
            int body = 0;
            for (int i = 0; i < chunks.Length; i++)
            {
                int raw = 4 + (chunks[i].Value == null ? 0 : chunks[i].Value.Length);
                body += (raw + 3) & ~3;
            }

            var packet = new byte[12 + body];
            WriteUInt16(packet, 0, sourcePort);
            WriteUInt16(packet, 2, destPort);
            WriteUInt32(packet, 4, verificationTag);
            // checksum zeroed while computing
            int offset = 12;
            for (int i = 0; i < chunks.Length; i++)
            {
                SctpChunk chunk = chunks[i];
                int valueLen = chunk.Value == null ? 0 : chunk.Value.Length;
                int chunkLen = 4 + valueLen;
                packet[offset] = chunk.Type;
                packet[offset + 1] = chunk.Flags;
                WriteUInt16(packet, offset + 2, (ushort)chunkLen);
                if (valueLen > 0)
                {
                    Buffer.BlockCopy(chunk.Value, 0, packet, offset + 4, valueLen);
                }

                offset += (chunkLen + 3) & ~3;
            }

            uint crc = SctpCrc32c.Compute(packet, 0, packet.Length);
            // SCTP writes CRC32C in little-endian on the wire (RFC 4960 §1.6 note + Linux/pion).
            packet[8] = (byte)crc;
            packet[9] = (byte)(crc >> 8);
            packet[10] = (byte)(crc >> 16);
            packet[11] = (byte)(crc >> 24);
            return packet;
        }

        public static bool TryParse(
            byte[] packet,
            out ushort sourcePort,
            out ushort destPort,
            out uint verificationTag,
            out List<SctpChunk> chunks)
        {
            sourcePort = 0;
            destPort = 0;
            verificationTag = 0;
            chunks = new List<SctpChunk>();
            if (packet == null || packet.Length < 12)
            {
                return false;
            }

            sourcePort = ReadUInt16(packet, 0);
            destPort = ReadUInt16(packet, 2);
            verificationTag = ReadUInt32(packet, 4);
            uint got = (uint)(packet[8] | (packet[9] << 8) | (packet[10] << 16) | (packet[11] << 24));
            packet[8] = packet[9] = packet[10] = packet[11] = 0;
            uint expect = SctpCrc32c.Compute(packet, 0, packet.Length);
            // restore so callers can still inspect (optional)
            packet[8] = (byte)got;
            packet[9] = (byte)(got >> 8);
            packet[10] = (byte)(got >> 16);
            packet[11] = (byte)(got >> 24);
            if (got != expect)
            {
                return false;
            }

            int offset = 12;
            while (offset + 4 <= packet.Length)
            {
                byte type = packet[offset];
                byte flags = packet[offset + 1];
                ushort chunkLen = ReadUInt16(packet, offset + 2);
                if (chunkLen < 4 || offset + chunkLen > packet.Length)
                {
                    break;
                }

                int valueLen = chunkLen - 4;
                var value = new byte[valueLen];
                if (valueLen > 0)
                {
                    Buffer.BlockCopy(packet, offset + 4, value, 0, valueLen);
                }

                chunks.Add(new SctpChunk { Type = type, Flags = flags, Value = value });
                offset += (chunkLen + 3) & ~3;
            }

            return chunks.Count > 0;
        }

        public static bool TryReadInitAckCookie(byte[] initAckValue, out byte[] cookie)
        {
            cookie = null;
            if (initAckValue == null || initAckValue.Length < 16)
            {
                return false;
            }

            int offset = 16;
            while (offset + 4 <= initAckValue.Length)
            {
                ushort type = ReadUInt16(initAckValue, offset);
                ushort length = ReadUInt16(initAckValue, offset + 2);
                if (length < 4 || offset + length > initAckValue.Length)
                {
                    return false;
                }

                if (type == 7)
                {
                    int cookieLen = length - 4;
                    cookie = new byte[cookieLen];
                    Buffer.BlockCopy(initAckValue, offset + 4, cookie, 0, cookieLen);
                    return true;
                }

                offset += (length + 3) & ~3;
            }

            return false;
        }

        public static void WriteUInt16(byte[] buf, int offset, ushort value)
        {
            buf[offset] = (byte)(value >> 8);
            buf[offset + 1] = (byte)value;
        }

        public static void WriteUInt32(byte[] buf, int offset, uint value)
        {
            buf[offset] = (byte)(value >> 24);
            buf[offset + 1] = (byte)(value >> 16);
            buf[offset + 2] = (byte)(value >> 8);
            buf[offset + 3] = (byte)value;
        }

        public static ushort ReadUInt16(byte[] buf, int offset) =>
            (ushort)((buf[offset] << 8) | buf[offset + 1]);

        public static uint ReadUInt32(byte[] buf, int offset) =>
            ((uint)buf[offset] << 24) |
            ((uint)buf[offset + 1] << 16) |
            ((uint)buf[offset + 2] << 8) |
            buf[offset + 3];
    }
}
