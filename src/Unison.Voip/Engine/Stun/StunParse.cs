// =============================================================================
// StunParse
//
// Relay STUN response classification and TLV parsing: allocate success/error,
// WhatsApp pong, attribute list and ERROR-CODE.
// =============================================================================
// Ports: meowcaller stun/stun.go (MIT)
using System;
using System.Collections.Generic;

namespace Unison.Voip.Engine.Stun
{
    /// <summary>One parsed STUN TLV.</summary>
    public sealed class StunAttribute
    {
        public StunAttribute(ushort attrType, byte[] value)
        {
            AttrType = attrType;
            Value = value;
        }

        public ushort AttrType { get; }

        public byte[] Value { get; }
    }

    public static class StunParse
    {
        private const ushort MsgBindingError = 0x0111;

        /// <summary>True when data looks like STUN (top two bits of the first byte zero).</summary>
        public static bool IsStunPacket(byte[] data)
        {
            return data != null && data.Length >= 2 && (data[0] & 0xc0) == 0x00;
        }

        /// <summary>STUN message type (14 bits); false if data is shorter than 2 bytes.</summary>
        public static bool StunMessageType(byte[] data, out ushort messageType)
        {
            if (data == null || data.Length < 2)
            {
                messageType = 0;
                return false;
            }
            messageType = (ushort)(((data[0] & 0x3f) << 8) | data[1]);
            return true;
        }

        /// <summary>Copy of the 12-byte transaction id; false if data is shorter than 20 bytes.</summary>
        public static bool StunTransactionID(byte[] data, out byte[] transactionId)
        {
            if (data == null || data.Length < StunCodec.HeaderLength)
            {
                transactionId = null;
                return false;
            }
            transactionId = new byte[StunCodec.TransactionIdLength];
            Buffer.BlockCopy(data, 8, transactionId, 0, StunCodec.TransactionIdLength);
            return true;
        }

        public static bool IsAllocateOrBindingSuccess(byte[] data)
        {
            if (!IsStunPacket(data) || data.Length < StunCodec.HeaderLength)
            {
                return false;
            }
            ushort mt;
            return StunMessageType(data, out mt)
                && (mt == StunCodec.MsgAllocateSuccess || mt == StunCodec.MsgBindingSuccess);
        }

        public static bool IsAllocateError(byte[] data)
        {
            if (!IsStunPacket(data))
            {
                return false;
            }
            ushort mt;
            return StunMessageType(data, out mt) && mt == StunCodec.MsgAllocateError;
        }

        /// <summary>WhatsApp pong (0x0802); a null/empty transactionId matches any.</summary>
        public static bool IsWhatsappPong(byte[] data, byte[] transactionId)
        {
            if (!IsStunPacket(data))
            {
                return false;
            }
            ushort mt;
            if (!StunMessageType(data, out mt) || mt != StunCodec.MsgWhatsappPong)
            {
                return false;
            }
            if (transactionId == null || transactionId.Length == 0)
            {
                return true;
            }
            if (data.Length < StunCodec.HeaderLength || transactionId.Length != StunCodec.TransactionIdLength)
            {
                return false;
            }
            for (int i = 0; i < StunCodec.TransactionIdLength; i++)
            {
                if (data[8 + i] != transactionId[i])
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Attributes after the 20-byte header; empty if not STUN. Ignores the header
        /// length field and stops at the first truncated attribute.
        /// </summary>
        public static IList<StunAttribute> ParseStunAttributes(byte[] data)
        {
            var attrs = new List<StunAttribute>();
            if (!IsStunPacket(data) || data.Length < StunCodec.HeaderLength)
            {
                return attrs;
            }
            int off = StunCodec.HeaderLength;
            while (off + 4 <= data.Length)
            {
                ushort attrType = (ushort)((data[off] << 8) | data[off + 1]);
                int length = (data[off + 2] << 8) | data[off + 3];
                off += 4;
                if (off + length > data.Length)
                {
                    break;
                }
                var value = new byte[length];
                Buffer.BlockCopy(data, off, value, 0, length);
                attrs.Add(new StunAttribute(attrType, value));
                off += length + StunCodec.Pad4(length);
            }
            return attrs;
        }

        /// <summary>
        /// ERROR-CODE as class*100+number from an Allocate (0x0113) or Binding (0x0111)
        /// error response; false if absent.
        /// </summary>
        public static bool ParseStunErrorCode(byte[] data, out ushort code)
        {
            code = 0;
            if (data == null || data.Length < StunCodec.HeaderLength)
            {
                return false;
            }
            ushort t;
            if (!StunMessageType(data, out t) || (t != StunCodec.MsgAllocateError && t != MsgBindingError))
            {
                return false;
            }
            int bodyLen = (data[2] << 8) | data[3];
            int end = Math.Min(StunCodec.HeaderLength + bodyLen, data.Length);
            int off = StunCodec.HeaderLength;
            while (off + 4 <= end)
            {
                ushort attrType = (ushort)((data[off] << 8) | data[off + 1]);
                int length = (data[off + 2] << 8) | data[off + 3];
                // Class is the whole byte (not RFC 5389's low 3 bits), as the reference does.
                if (attrType == StunCodec.AttrErrorCode && length >= 4 && off + 8 <= data.Length)
                {
                    code = (ushort)(data[off + 6] * 100 + data[off + 7]);
                    return true;
                }
                off += 4 + length + StunCodec.Pad4(length);
            }
            return false;
        }
    }
}
