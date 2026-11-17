// =============================================================================
// StunProtobuf
//
// Hand-rolled protobuf writer for the STUN allocate payloads (sender
// subscriptions, stream descriptors). Field order and omitted zero fields are
// part of the KAT wire shape, so no generated proto types here.
// =============================================================================
// Ports: meowcaller stun/stun.go (MIT)
using System.Collections.Generic;

namespace Unison.Voip.Engine.Stun
{
    internal static class StunProtobuf
    {
        public const uint WireVarint = 0;
        public const uint WireLengthDelimited = 2;

        public static void AppendUvarint(List<byte> output, ulong value)
        {
            while (value >= 0x80)
            {
                output.Add((byte)(value | 0x80));
                value >>= 7;
            }
            output.Add((byte)value);
        }

        public static void PbTag(List<byte> output, uint field, uint wire)
        {
            AppendUvarint(output, (field << 3) | wire);
        }

        public static void PbVarintField(List<byte> output, uint field, ulong value)
        {
            PbTag(output, field, WireVarint);
            AppendUvarint(output, value);
        }

        public static ulong PbZigzag(long n)
        {
            return (ulong)((n << 1) ^ (n >> 63));
        }

        public static void PbLenDelim(List<byte> output, uint field, byte[] value)
        {
            PbTag(output, field, WireLengthDelimited);
            AppendUvarint(output, (ulong)value.Length);
            output.AddRange(value);
        }

        public static void PbLenDelim(List<byte> output, uint field, List<byte> value)
        {
            PbLenDelim(output, field, value.ToArray());
        }

        public static byte[] LenDelim(uint field, List<byte> value)
        {
            var output = new List<byte>(value.Count + 6);
            PbLenDelim(output, field, value);
            return output.ToArray();
        }
    }
}
