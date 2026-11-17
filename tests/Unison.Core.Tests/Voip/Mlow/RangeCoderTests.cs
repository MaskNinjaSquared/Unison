using System.Text.Json;
using Unison.Voip.Engine.Mlow;

namespace Unison.Core.Tests.Voip.Mlow
{
    public class RangeCoderTests
    {
        private static JsonElement Vectors()
        {
            return MlowKats.LoadJson("rc_vectors.json");
        }

        private static byte[] Icdf(JsonElement v)
        {
            byte[] icdf = v.GetProperty("icdf").GetBytesFromBase64();
            Assert.Equal(6, icdf.Length);
            return icdf;
        }

        private static ushort[][] CdfTables(JsonElement v)
        {
            var t = v.GetProperty("cdfTables");
            var o = new ushort[t.GetArrayLength()][];
            int i = 0;
            foreach (var row in t.EnumerateArray())
            {
                o[i++] = row.UShorts();
            }

            return o;
        }

        [Fact]
        public void RangeDecoderMatchesVectors()
        {
            var v = Vectors();
            byte[] icdf = Icdf(v);
            var d = new RangeDecoder(MlowKats.Hex(v.GetProperty("bytesHex").GetString()!));
            int i = 0;
            foreach (var op in v.GetProperty("ops").EnumerateArray())
            {
                int kind = op.GetProperty("kind").GetInt32();
                uint a = op.GetProperty("a").GetUInt32();
                uint b = op.GetProperty("b").GetUInt32();
                switch (kind)
                {
                    case 0:
                        Assert.True(d.DecodeIcdf(icdf, 8) == (int)a, "op " + i + " icdf");
                        break;
                    case 1:
                        Assert.True(d.BitsN(a) == b, "op " + i + " bits");
                        break;
                    case 2:
                        Assert.True((uint)d.BitLogp(a) == b, "op " + i + " bit_logp");
                        break;
                    case 3:
                        Assert.True(d.DecodeUint(a) == b, "op " + i + " uint");
                        break;
                    default:
                        Assert.Fail("bad kind " + kind);
                        break;
                }

                i++;
            }

            Assert.Equal(0, d.Err);
        }

        [Fact]
        public void RangeDecoderCdfMatchesVectors()
        {
            var v = Vectors();
            var tables = CdfTables(v);
            var d = new RangeDecoder(MlowKats.Hex(v.GetProperty("cdfBytesHex").GetString()!));
            int i = 0;
            foreach (var op in v.GetProperty("cdfOps").EnumerateArray())
            {
                int ti = op.GetProperty("kind").GetInt32();
                int a = op.GetProperty("a").GetInt32();
                Assert.True(d.DecodeCdf(tables[ti]) == a, "cdf op " + i);
                i++;
            }

            Assert.Equal(0, d.Err);
        }

        [Fact]
        public void RangeEncoderMatchesBytes()
        {
            var v = Vectors();
            byte[] icdf = Icdf(v);
            byte[] want = MlowKats.Hex(v.GetProperty("bytesHex").GetString()!);
            var e = new RangeEncoder(want.Length);
            foreach (var op in v.GetProperty("ops").EnumerateArray())
            {
                int kind = op.GetProperty("kind").GetInt32();
                uint a = op.GetProperty("a").GetUInt32();
                uint b = op.GetProperty("b").GetUInt32();
                switch (kind)
                {
                    case 0: e.EncodeIcdf((int)a, icdf, 8); break;
                    case 1: e.BitsN(b, a); break;
                    case 2: e.BitLogp((int)b, a); break;
                    case 3: e.EncodeUint(b, a); break;
                    default: Assert.Fail("bad kind " + kind); break;
                }
            }

            e.Done();
            Assert.Equal(0, e.Err);
            Assert.Equal(want, e.Bytes());
        }

        [Fact]
        public void RangeEncoderCdfMatchesBytes()
        {
            var v = Vectors();
            var tables = CdfTables(v);
            byte[] want = MlowKats.Hex(v.GetProperty("cdfBytesHex").GetString()!);
            var e = new RangeEncoder(want.Length);
            foreach (var op in v.GetProperty("cdfOps").EnumerateArray())
            {
                e.EncodeCdf(op.GetProperty("a").GetInt32(), tables[op.GetProperty("kind").GetInt32()]);
            }

            e.Done();
            Assert.Equal(0, e.Err);
            Assert.Equal(want, e.Bytes());
        }
    }
}
