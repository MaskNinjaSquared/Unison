// =============================================================================
// StunCodec
//
// WhatsApp relay STUN/WARP framing: RFC 5389 TLV encoder with WhatsApp's
// MESSAGE-INTEGRITY (HMAC-SHA1) and FINGERPRINT (CRC-32), XOR relay endpoint,
// native sender subscription and the consent ping.
// =============================================================================
// Ports: meowcaller stun/stun.go (MIT)
using System;
using System.Security.Cryptography;

namespace Unison.Voip.Engine.Stun
{
    public static class StunCodec
    {
        public const ushort MsgBindingRequest = 0x0001;
        public const ushort MsgAllocateRequest = 0x0003;
        public const ushort MsgBindingSuccess = 0x0101;
        public const ushort MsgAllocateSuccess = 0x0103;
        public const ushort MsgAllocateError = 0x0113;
        public const ushort MsgWhatsappPing = 0x0801;
        public const ushort MsgWhatsappPong = 0x0802;

        public const ushort AttrMessageIntegrity = 0x0008;
        public const ushort AttrFingerprint = 0x8028;
        public const ushort AttrErrorCode = 0x0009;
        public const ushort AttrRelayToken = 0x4000;
        public const ushort AttrReceiverSubscriptions = 0x4021;
        public const ushort AttrStreamDescriptors = 0x4024;
        public const ushort AttrWasmRelayEndpoint = 0x0016;
        public const ushort AttrSenderSubscriptionsV2 = 0x4025;
        public const ushort AttrParticipantCount = 0x805a;

        public const int HeaderLength = 20;
        public const int TransactionIdLength = 12;
        public const int XorRelayEndpointLength = 6;

        internal const uint StunMagic = 0x2112a442;
        private const uint StunFingerprintXor = 0x5354554e;
        private const ushort StunXorPort = 0x2112;

        private static readonly byte[] StunXorAddr = { 0x21, 0x12, 0xa4, 0x42 };

        internal static int Pad4(int n)
        {
            return (4 - (n % 4)) % 4;
        }

        /// <summary>One STUN attribute: type, length, value, 4-byte alignment pad.</summary>
        public static byte[] StunAttr(ushort attrType, byte[] value)
        {
            int valueLength = value == null ? 0 : value.Length;
            var buf = new byte[4 + valueLength + Pad4(valueLength)];
            WriteUInt16Be(buf, 0, attrType);
            WriteUInt16Be(buf, 2, (ushort)valueLength);
            if (valueLength > 0)
            {
                Buffer.BlockCopy(value, 0, buf, 4, valueLength);
            }
            return buf;
        }

        /// <summary>STUN FINGERPRINT CRC-32 (reflected IEEE poly 0xedb88320), before the 0x5354554e XOR.</summary>
        public static uint StunFingerprint(byte[] buf)
        {
            return Crc32Ieee.Compute(buf, 0, buf == null ? 0 : buf.Length);
        }

        /// <summary>
        /// Header + attrs, then optional MESSAGE-INTEGRITY (null integrityKey skips it)
        /// and optional FINGERPRINT.
        /// </summary>
        public static byte[] EncodeStunRequest(
            ushort msgType,
            byte[] transactionId,
            byte[] attrs,
            byte[] integrityKey,
            bool includeFingerprint)
        {
            RequireLength(transactionId, TransactionIdLength, nameof(transactionId));
            byte[] body = attrs == null ? new byte[0] : (byte[])attrs.Clone();

            if (integrityKey != null)
            {
                // attrs + MI attr (4 + 20)
                ushort msgLen = (ushort)(body.Length + 24);
                byte[] macInput = Concat(StunPseudoHeader(msgType, msgLen, transactionId), body);
                byte[] mac;
                using (var hmac = new HMACSHA1(integrityKey))
                {
                    mac = hmac.ComputeHash(macInput);
                }
                body = Concat(body, StunAttr(AttrMessageIntegrity, mac));
            }

            if (includeFingerprint)
            {
                // attrs + FINGERPRINT attr (4 + 4)
                ushort msgLen = (ushort)(body.Length + 8);
                byte[] crcInput = Concat(StunPseudoHeader(msgType, msgLen, transactionId), body);
                uint fp = StunFingerprint(crcInput) ^ StunFingerprintXor;
                var fpb = new byte[4];
                WriteUInt32Be(fpb, 0, fp);
                body = Concat(body, StunAttr(AttrFingerprint, fpb));
            }

            return Concat(StunPseudoHeader(msgType, (ushort)body.Length, transactionId), body);
        }

        /// <summary>Native WA sender subscription: 1-byte count + BE SSRC.</summary>
        public static byte[] CreateNativeSenderSubscription(uint ssrc)
        {
            var buf = new byte[5];
            buf[0] = 1;
            WriteUInt32Be(buf, 1, ssrc);
            return buf;
        }

        /// <summary>
        /// XOR-encodes IPv4:port into 6 bytes. False (and six zero bytes) unless
        /// exactly four dotted octets parse; unparsable parts are skipped, as in Go.
        /// </summary>
        public static bool EncodeXorRelayEndpoint(string ipv4, ushort port, out byte[] endpointXor)
        {
            endpointXor = new byte[XorRelayEndpointLength];
            if (ipv4 == null)
            {
                return false;
            }

            var octets = new byte[4];
            int count = 0;
            foreach (string part in ipv4.Split('.'))
            {
                byte octet;
                if (!TryParseDecimalOctet(part, out octet))
                {
                    continue;
                }
                if (count == 4)
                {
                    return false;
                }
                octets[count++] = octet;
            }
            if (count != 4)
            {
                return false;
            }

            WriteUInt16Be(endpointXor, 0, (ushort)(port ^ StunXorPort));
            for (int i = 0; i < 4; i++)
            {
                endpointXor[2 + i] = (byte)(octets[i] ^ StunXorAddr[i]);
            }
            return true;
        }

        /// <summary>WhatsApp consent ping: type 0x0801, empty body.</summary>
        public static byte[] BuildWhatsappPing(byte[] transactionId)
        {
            RequireLength(transactionId, TransactionIdLength, nameof(transactionId));
            return StunPseudoHeader(MsgWhatsappPing, 0, transactionId);
        }

        internal static void RequireLength(byte[] value, int length, string paramName)
        {
            if (value == null || value.Length != length)
            {
                throw new ArgumentException("Expected exactly " + length + " bytes.", paramName);
            }
        }

        internal static void RequireLength(uint[] value, int length, string paramName)
        {
            if (value == null || value.Length != length)
            {
                throw new ArgumentException("Expected exactly " + length + " values.", paramName);
            }
        }

        internal static byte[] Concat(byte[] a, byte[] b)
        {
            var result = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, result, 0, a.Length);
            Buffer.BlockCopy(b, 0, result, a.Length, b.Length);
            return result;
        }

        private static byte[] StunPseudoHeader(ushort msgType, ushort msgLen, byte[] transactionId)
        {
            var h = new byte[HeaderLength];
            WriteUInt16Be(h, 0, msgType);
            WriteUInt16Be(h, 2, msgLen);
            WriteUInt32Be(h, 4, StunMagic);
            Buffer.BlockCopy(transactionId, 0, h, 8, TransactionIdLength);
            return h;
        }

        // Go strconv.ParseUint(part, 10, 8): ASCII digits only, no sign, value <= 255.
        private static bool TryParseDecimalOctet(string part, out byte octet)
        {
            octet = 0;
            if (part.Length == 0)
            {
                return false;
            }
            int value = 0;
            foreach (char c in part)
            {
                if (c < '0' || c > '9')
                {
                    return false;
                }
                value = value * 10 + (c - '0');
                if (value > byte.MaxValue)
                {
                    return false;
                }
            }
            octet = (byte)value;
            return true;
        }

        private static void WriteUInt16Be(byte[] buf, int offset, ushort value)
        {
            buf[offset] = (byte)(value >> 8);
            buf[offset + 1] = (byte)value;
        }

        private static void WriteUInt32Be(byte[] buf, int offset, uint value)
        {
            buf[offset] = (byte)(value >> 24);
            buf[offset + 1] = (byte)(value >> 16);
            buf[offset + 2] = (byte)(value >> 8);
            buf[offset + 3] = (byte)value;
        }

        private static class Crc32Ieee
        {
            private static readonly uint[] Table = BuildTable();

            public static uint Compute(byte[] data, int offset, int count)
            {
                uint crc = 0xffffffff;
                for (int i = offset; i < offset + count; i++)
                {
                    crc = Table[(crc ^ data[i]) & 0xff] ^ (crc >> 8);
                }
                return ~crc;
            }

            private static uint[] BuildTable()
            {
                var table = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++)
                    {
                        c = (c & 1) != 0 ? 0xedb88320 ^ (c >> 1) : c >> 1;
                    }
                    table[n] = c;
                }
                return table;
            }
        }
    }
}
