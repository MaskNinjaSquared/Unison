using System;
using System.Collections.Generic;
using Unison.Voip.Engine.Mlow;

namespace Unison.Core.Tests.Voip.Mlow
{
    public class MlowDecoderTests
    {
        private const int FrameSamples = 960;

        [Fact]
        public void E2EDecodeMatchesUseSmplReference()
        {
            string[] frames = MlowKats.LoadJson("inbound_capture_frames.json").Strings();
            byte[] raw = MlowKats.ReadBytes("ref_usesmpl_expected.raw");
            var refp = new double[raw.Length / 2];
            for (int i = 0; i < refp.Length; i++)
            {
                refp[i] = BitConverter.ToInt16(raw, 2 * i) / 32768.0;
            }

            var dec = new MlowDecoder();
            var output = new List<double>();
            foreach (string hf in frames)
            {
                foreach (float v in dec.Decode(MlowKats.Hex(hf)))
                {
                    output.Add(v);
                }
            }

            Assert.Equal(refp.Length, output.Count);
            int n = refp.Length;
            double mr = 0, mo = 0;
            for (int i = 0; i < n; i++)
            {
                mr += refp[i];
                mo += output[i];
            }

            mr /= n;
            mo /= n;
            double sxy = 0, sxx = 0, syy = 0;
            for (int i = 0; i < n; i++)
            {
                double dr = refp[i] - mr;
                double dz = output[i] - mo;
                sxy += dr * dz;
                sxx += dr * dr;
                syy += dz * dz;
            }

            double denom = Math.Sqrt(sxx * syy);
            Assert.True(denom != 0, "constant signal");
            double corr = sxy / denom;
            Assert.True(corr > 0.95, "lag-0 corr " + corr);
        }

        [Fact]
        public void EmptyPayloadDecodesToSilence()
        {
            float[] pcm = new MlowDecoder().Decode(new byte[0]);
            Assert.Equal(FrameSamples, pcm.Length);
            Assert.All(pcm, v => Assert.Equal(0f, v));
        }

        [Fact]
        public void SplitContainerDecodesEverySubFrame()
        {
            byte[] active = FirstActiveFrame();
            var container = new List<byte> { 0x92, 2, (byte)active.Length };
            container.AddRange(active);
            container.AddRange(active);
            Assert.Equal(2 * FrameSamples, new MlowDecoder().Decode(container.ToArray()).Length);
        }

        private static byte[] FirstActiveFrame()
        {
            foreach (string hf in MlowKats.LoadJson("inbound_capture_frames.json").Strings())
            {
                byte[] f = MlowKats.Hex(hf);
                if (f.Length < 2)
                {
                    continue;
                }

                SmplToc toc = SmplToc.Parse(f[0]);
                if (!toc.StdOpus && !toc.Sid && toc.Active && toc.SampleRate == 16000 && toc.FrameMs == 60 && f.Length < 256)
                {
                    return f;
                }
            }

            throw new InvalidOperationException("no active frame in capture");
        }
    }
}
