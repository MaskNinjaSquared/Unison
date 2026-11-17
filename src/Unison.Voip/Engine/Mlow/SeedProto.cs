// =============================================================================
// SeedProto
//
// Embedded seed ROM loading: zlib inflate (2-byte header skipped, raw deflate)
// plus a minimal protobuf wire reader (varint, length-delimited, packed
// varint/zigzag/fixed32) for the lsf/pitch/cc seed messages.
// Ports: meowcaller mlow/cc_tables.go, lsf_seed.go, pitch_seed.go (seed parse, MIT)
// =============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Unison.Voip.Engine.Mlow
{
    internal struct ProtoField
    {
        public int Wire;
        public ulong Varint;
        public byte[] Bytes;
    }

    internal static class SeedProto
    {
        private const string ResourcePrefix = "Unison.Voip.Engine.Mlow.Seeds.";

        public static byte[] LoadInflated(string seedName)
        {
            using (Stream s = typeof(SeedProto).Assembly.GetManifestResourceStream(ResourcePrefix + seedName))
            {
                if (s == null)
                {
                    throw new InvalidOperationException("mlow: embedded seed missing: " + seedName);
                }

                if (s.ReadByte() < 0 || s.ReadByte() < 0)
                {
                    throw new InvalidDataException("mlow: truncated zlib header in " + seedName);
                }

                using (var inflate = new DeflateStream(s, CompressionMode.Decompress))
                using (var ms = new MemoryStream())
                {
                    inflate.CopyTo(ms);
                    return ms.ToArray();
                }
            }
        }

        private static bool ReadVarint(byte[] b, ref int i, out ulong v)
        {
            v = 0;
            int shift = 0;
            while (i < b.Length)
            {
                byte c = b[i];
                i++;
                if (shift < 64)
                {
                    v |= (ulong)(c & 0x7f) << shift;
                }

                if ((c & 0x80) == 0)
                {
                    return true;
                }

                shift += 7;
            }

            v = 0;
            return false;
        }

        /// <summary>Fields keyed by number; wiretype 0 (varint) and 2 (bytes) only, stops at anything else.</summary>
        public static Dictionary<int, ProtoField> ParseProto(byte[] b)
        {
            var o = new Dictionary<int, ProtoField>();
            int i = 0;
            while (i < b.Length)
            {
                ulong key;
                if (!ReadVarint(b, ref i, out key))
                {
                    break;
                }

                int field = (int)(key >> 3);
                int wire = (int)(key & 7);
                if (wire == 0)
                {
                    ulong v;
                    if (!ReadVarint(b, ref i, out v))
                    {
                        return o;
                    }

                    o[field] = new ProtoField { Wire = 0, Varint = v };
                }
                else if (wire == 2)
                {
                    ulong ln;
                    if (!ReadVarint(b, ref i, out ln) || (ulong)i + ln > (ulong)b.Length)
                    {
                        return o;
                    }

                    o[field] = new ProtoField { Wire = 2, Bytes = Slice(b, i, (int)ln) };
                    i += (int)ln;
                }
                else
                {
                    return o;
                }
            }

            return o;
        }

        /// <summary>Length-delimited fields only (the pitch seed); stops at the first other wiretype.</summary>
        public static Dictionary<int, byte[]> ParseProtoBytes(byte[] b)
        {
            var o = new Dictionary<int, byte[]>();
            int i = 0;
            while (i < b.Length)
            {
                ulong key;
                if (!ReadVarint(b, ref i, out key))
                {
                    break;
                }

                int field = (int)(key >> 3);
                int wire = (int)(key & 7);
                if (wire != 2)
                {
                    break;
                }

                ulong ln;
                if (!ReadVarint(b, ref i, out ln) || (ulong)i + ln > (ulong)b.Length)
                {
                    break;
                }

                o[field] = Slice(b, i, (int)ln);
                i += (int)ln;
            }

            return o;
        }

        public static byte[] BytesOf(Dictionary<int, ProtoField> f, int field)
        {
            ProtoField v;
            return f.TryGetValue(field, out v) && v.Bytes != null ? v.Bytes : new byte[0];
        }

        public static ulong VarintOf(Dictionary<int, ProtoField> f, int field)
        {
            ProtoField v;
            return f.TryGetValue(field, out v) ? v.Varint : 0;
        }

        public static byte[] BytesOf(Dictionary<int, byte[]> f, int field)
        {
            byte[] v;
            return f.TryGetValue(field, out v) ? v : new byte[0];
        }

        /// <summary>Packed repeated sint32 (zigzag varints).</summary>
        public static int[] DecodeZigzagVarints(byte[] b)
        {
            var o = new List<int>();
            int i = 0;
            while (i < b.Length)
            {
                ulong v = ReadPackedVarint(b, ref i);
                o.Add(unchecked((int)((long)(v >> 1) ^ -(long)(v & 1))));
            }

            return o.ToArray();
        }

        /// <summary>Packed repeated uint32 (plain varints).</summary>
        public static uint[] DecodeVarintsU32(byte[] b)
        {
            var o = new List<uint>();
            int i = 0;
            while (i < b.Length)
            {
                o.Add(unchecked((uint)ReadPackedVarint(b, ref i)));
            }

            return o.ToArray();
        }

        /// <summary>Packed repeated float (fixed32 little-endian).</summary>
        public static float[] DecodeFloats(byte[] b)
        {
            var o = new float[b.Length / 4];
            for (int i = 0; i < o.Length; i++)
            {
                uint u = (uint)(b[i * 4] | b[i * 4 + 1] << 8 | b[i * 4 + 2] << 16 | b[i * 4 + 3] << 24);
                o[i] = MlowMath.Float32FromBits(u);
            }

            return o;
        }

        private static ulong ReadPackedVarint(byte[] b, ref int i)
        {
            ulong v = 0;
            int shift = 0;
            while (i < b.Length)
            {
                byte c = b[i];
                i++;
                if (shift < 64)
                {
                    v |= (ulong)(c & 0x7f) << shift;
                }

                if ((c & 0x80) == 0)
                {
                    break;
                }

                shift += 7;
            }

            return v;
        }

        private static byte[] Slice(byte[] b, int offset, int count)
        {
            var o = new byte[count];
            Buffer.BlockCopy(b, offset, o, 0, count);
            return o;
        }
    }
}
