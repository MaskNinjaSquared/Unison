using Unison.Voip.Engine.Mlow;

namespace Unison.Core.Tests.Voip.Mlow
{
    public class TocAndRedTests
    {
        [Fact]
        public void ParseSmplTocMatchesAll256Vectors()
        {
            var vecs = MlowKats.LoadJson("toc_vectors.json");
            Assert.Equal(256, vecs.GetArrayLength());
            foreach (var v in vecs.EnumerateArray())
            {
                byte b = v.GetProperty("b").GetByte();
                SmplToc got = SmplToc.Parse(b);
                string at = "b=0x" + b.ToString("x2");
                Assert.True(got.StdOpus == v.GetProperty("std").GetBoolean(), at + " StdOpus");
                Assert.True(got.Sid == v.GetProperty("sid").GetBoolean(), at + " SID");
                Assert.True(got.Vad == v.GetProperty("vad").GetBoolean(), at + " VAD");
                Assert.True(got.SampleRate == v.GetProperty("sr").GetInt32(), at + " SampleRate");
                Assert.True(got.FrameMs == v.GetProperty("ms").GetInt32(), at + " FrameMs");
                Assert.True(got.Voiced == v.GetProperty("voiced").GetBoolean(), at + " Voiced");
                Assert.True(got.Active == v.GetProperty("active").GetBoolean(), at + " Active");
                Assert.True(got.Flag2 == v.GetProperty("f2").GetBoolean(), at + " Flag2");
                Assert.True(got.Flag0 == v.GetProperty("f0").GetBoolean(), at + " Flag0");
            }
        }

        [Fact]
        public void DepackSplitRedOneRedundantPlusMain()
        {
            Assert.True(SplitRed.TryDepack(
                new byte[] { 0x85, 0x03, 0x00, 0xAA, 0xBB, 0xCC, 0x50, 0x11, 0x22, 0x33 },
                out MlowFrame[] frames,
                out SplitRedError err));
            Assert.Equal(SplitRedError.None, err);
            Assert.Equal(2, frames.Length);
            Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, frames[0].Data);
            Assert.Equal(5, frames[0].TimeCode);
            Assert.False(frames[0].IsMain);
            Assert.Equal(new byte[] { 0x50, 0x11, 0x22, 0x33 }, frames[1].Data);
            Assert.Equal(0, frames[1].TimeCode);
            Assert.True(frames[1].IsMain);
        }

        [Fact]
        public void DepackSplitRedHeaderOnlyMain()
        {
            Assert.True(SplitRed.TryDepack(new byte[] { 0x00, 0x50, 0x11, 0x22 }, out MlowFrame[] frames, out _));
            Assert.Single(frames);
            Assert.True(frames[0].IsMain);
            Assert.Equal(new byte[] { 0x50, 0x11, 0x22 }, frames[0].Data);
        }

        [Fact]
        public void DepackSplitRedEmptyPacket()
        {
            Assert.False(SplitRed.TryDepack(new byte[0], out _, out SplitRedError err));
            Assert.Equal(SplitRedError.PktSizeZero, err);
            Assert.False(SplitRed.TryDepack(null!, out _, out err));
            Assert.Equal(SplitRedError.PktSizeZero, err);
        }

        [Fact]
        public void DepackSplitRedRejectsBareHighBitFrame()
        {
            Assert.False(SplitRed.TryDepack(new byte[] { 0x90, 0x01, 0x02 }, out _, out SplitRedError err));
            Assert.NotEqual(SplitRedError.None, err);
        }
    }
}
