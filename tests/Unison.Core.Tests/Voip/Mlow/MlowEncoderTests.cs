using System;
using System.Diagnostics;
using Unison.Voip.Engine.Media;
using Unison.Voip.Engine.Mlow;
using Xunit.Abstractions;

namespace Unison.Core.Tests.Voip.Mlow
{
    public class MlowEncoderTests
    {
        private const int FrameSamples = 960;

        private readonly ITestOutputHelper _out;

        public MlowEncoderTests(ITestOutputHelper output)
        {
            _out = output;
        }

        [Fact]
        public void SignalModeMatchesCGroundTruth()
        {
            var recs = MlowKats.LoadJson("sigmode_ground_truth.json");
            Assert.True(recs.GetArrayLength() >= 12);
            var vuv = new VuvMode();
            float maxErr = 0, maxHarmErr = 0;
            foreach (var rec in recs.EnumerateArray())
            {
                float[] f2 = rec.GetProperty("F2").Floats();
                Assert.Equal(SmplLpc.FLen, f2.Length);
                float avgLag = rec.GetProperty("avg_lag").GetSingle();
                float harm = rec.GetProperty("harm").GetSingle();
                if (avgLag > 33.0f)
                {
                    float hs = SmplSignalMode.HarmStrengthAt(avgLag, SmplSignalMode.BuildF2w(f2));
                    maxHarmErr = Math.Max(maxHarmErr, Math.Abs(hs - harm));
                }

                float vstr = SmplSignalMode.SmplGetSignalMode(
                    rec.GetProperty("pitchcorr").GetSingle(),
                    rec.GetProperty("lags").Floats(),
                    avgLag,
                    harm,
                    f2,
                    rec.GetProperty("sp_act_prob").GetSingle(),
                    vuv);
                float want = rec.GetProperty("vstr").GetSingle();
                maxErr = Math.Max(maxErr, Math.Abs(vstr - want));
                Assert.True((vstr > 0.0f) == (rec.GetProperty("voiced").GetInt32() != 0), "frame " + rec.GetProperty("frame").GetInt32() + " voiced flip");
            }

            Assert.True(maxErr < 1e-4f, "voicing_strength max_err " + maxErr);
            Assert.True(maxHarmErr < 0.05f, "harm_strength max_err " + maxHarmErr);
        }

        [Fact]
        public void PitchBlockRoundTripsContour()
        {
            SmplMem mem = SmplMem.Load();
            var cases = new[]
            {
                new { Bsx = 142, Laginds = new[] { 128, 129, 129, 118, 118, 121, 121, 123 }, Gains = new[] { 5, 2, 2, 2 } },
                new { Bsx = 142, Laginds = new[] { 128, 129, 129, 118, 118, 121, 121, 123 }, Gains = new[] { 5, 6, 2, 2 } },
                new { Bsx = 59, Laginds = new[] { 123, 123, 123, 123, 128, 128, 132, 132 }, Gains = new[] { 2, 6, 6, 6 } },
            };
            var subfr = new[] { 1, 1, 1, 1 };
            foreach (var c in cases)
            {
                var pp = new SmplPitchParams { BlocksegIdx = c.Bsx };
                Array.Copy(c.Gains, pp.GainIdx, 4);
                Array.Copy(c.Laginds, pp.Laginds, 8);
                var est = new SmplLsfState { PrevLag = -1, PrevFracLag = -1, PrevLagblk = -1, PrevLagidx = -1 };
                var enc = new RangeEncoder(64);
                SmplEncode.EncodeSmplPitch(enc, mem, est, 320, 4, 0, subfr, pp);
                enc.Done();
                Assert.Equal(0, enc.Err);
                var body = new byte[enc.ConsumedLen()];
                Array.Copy(enc.Bytes(), body, body.Length);

                var dec = new RangeDecoder(body);
                var dst = new SmplLsfState { PrevLag = -1, PrevFracLag = -1 };
                SmplPitchResult pr = SmplPitch.DecodeSmplPitch(dec, mem, dst, 320, 4, 0, subfr);
                Assert.Equal(c.Laginds, pr.BlockLags);
            }
        }

        private static SmplFrameParams CaptureUnvoicedFrame(byte[] fb)
        {
            SmplToc toc = SmplToc.Parse(fb[0]);
            if (toc.StdOpus || toc.Sid || !toc.Active)
            {
                return null;
            }

            int config = (fb[0] >> 2) & 1;
            SmplTables tbl = SmplLsf.LoadSmplTables();
            SmplMem mem = SmplMem.Load();
            var dec = new RangeDecoder(fb, 1, fb.Length - 1);
            var st = new SmplLsfState();
            var fp = new SmplFrameParams { Toc = fb[0], Config = config };
            for (int f = 0; f < 3; f++)
            {
                SmplLsfIndices lsf = SmplLsf.DecodeSmplLsf(dec, tbl, st, config, f);
                SmplPulseResult pul = SmplPulse.DecodeSmplPulses(dec, mem, SmplSynth.SmplIntfLen, 4, 1, config, lsf.Stage1);
                SmplInternalParams ip = fp.Internal[f];
                ip.Lsf.Stage1 = lsf.Stage1;
                ip.Lsf.Grid = lsf.Grid;
                Array.Copy(lsf.Stage2, ip.Lsf.Stage2, 16);
                ip.Lsf.Extra = lsf.Extra;
                int total = 0;
                foreach (int c in pul.Subfr)
                {
                    total += c;
                }

                ip.Pulses.Total = total;
                Array.Copy(pul.Subfr, ip.Pulses.Subfr, 4);
                ip.Pulses.MagRuns.AddRange(pul.MagRuns);
                ip.Pulses.SignSyms.AddRange(pul.SignSyms);
                if (lsf.Stage1 == 1)
                {
                    return null;
                }

                SmplGainResult g = SmplGains.DecodeSmplGains(dec, mem, 4, pul.Subfr);
                ip.Gains.GainMain = g.GainMain;
                ip.Gains.GainDelta = g.GainDelta;
                Array.Copy(g.NrgRes, ip.Gains.NrgRes, 4);
            }

            return fp;
        }

        private static bool SameFrameParams(SmplFrameParams a, SmplFrameParams b)
        {
            if (a.Toc != b.Toc || a.Config != b.Config)
            {
                return false;
            }

            for (int f = 0; f < 3; f++)
            {
                SmplInternalParams x = a.Internal[f], y = b.Internal[f];
                if (x.Lsf.Stage1 != y.Lsf.Stage1 || x.Lsf.Grid != y.Lsf.Grid || x.Lsf.Extra != y.Lsf.Extra
                    || !x.Lsf.Stage2.AsSpan().SequenceEqual(y.Lsf.Stage2))
                {
                    return false;
                }

                if (x.Gains.GainMain != y.Gains.GainMain || x.Gains.GainDelta != y.Gains.GainDelta
                    || !x.Gains.NrgRes.AsSpan().SequenceEqual(y.Gains.NrgRes))
                {
                    return false;
                }

                if (x.Pulses.Total != y.Pulses.Total || !x.Pulses.Subfr.AsSpan().SequenceEqual(y.Pulses.Subfr))
                {
                    return false;
                }
            }

            return true;
        }

        [Fact]
        public void EntropyEncoderIsByteExactInverseOfDecoder()
        {
            int checkedN = 0;
            foreach (string hf in MlowKats.LoadJson("inbound_capture_frames.json").Strings())
            {
                if (hf.Length == 0)
                {
                    continue;
                }

                byte[] fb = MlowKats.Hex(hf);
                SmplFrameParams fp = CaptureUnvoicedFrame(fb);
                if (fp == null)
                {
                    continue;
                }

                byte[] got = SmplEncode.EncodeSmplFrame(fp);
                Assert.NotNull(got);
                Assert.True(got.Length >= fb.Length && got.AsSpan(0, fb.Length).SequenceEqual(fb), "re-encode prefix differs: " + Convert.ToHexString(got) + " vs " + hf);
                for (int j = fb.Length; j < got.Length; j++)
                {
                    Assert.Equal(0, got[j]);
                }

                SmplFrameParams rt = CaptureUnvoicedFrame(got);
                Assert.NotNull(rt);
                Assert.True(SameFrameParams(fp, rt), "re-decode params differ");
                checkedN++;
            }

            Assert.True(checkedN >= 20, "too few unvoiced frames: " + checkedN);
        }

        private static double ToneCorr(float[] a, int aOff, float[] b, int bOff, int n)
        {
            double sxy = 0, sxx = 0, syy = 0;
            for (int i = 0; i < n; i++)
            {
                double x = a[aOff + i], y = b[bOff + i];
                sxy += x * y;
                sxx += x * x;
                syy += y * y;
            }

            if (sxx < 1e-12 || syy < 1e-12)
            {
                return 0.0;
            }

            return sxy / Math.Sqrt(sxx * syy);
        }

        [Fact]
        public void EncodeRoundTripsATone()
        {
            var enc = new MlowEncoder();
            var dec = new MlowDecoder();
            double best = 0;
            for (int f = 0; f < 8; f++)
            {
                var pcm = new float[FrameSamples];
                for (int i = 0; i < FrameSamples; i++)
                {
                    double tt = (double)(f * FrameSamples + i) / 16000.0;
                    pcm[i] = (float)(0.5 * Math.Sin(2.0 * Math.PI * 550.0 * tt));
                }

                byte[] frame = enc.Encode(pcm);
                Assert.NotNull(frame);
                Assert.True(frame.Length > 0 && frame[0] == 0x50, "frame " + f + " TOC");
                float[] o = dec.Decode(frame);
                const int harmDelay = 48;
                best = Math.Max(best, ToneCorr(pcm, 0, o, harmDelay, FrameSamples - harmDelay));
            }

            _out.WriteLine("encode->decode best tone correlation: " + best.ToString("F4"));
            Assert.True(best > 0.7, "round-trip correlation too low: " + best);
        }

        /// <summary>
        /// go_encoder_frames.bin: u32 count, then per frame 960 float32 input samples, u32 length and the
        /// meowcaller Go encoder's output for that frame (mic_clip.raw, 8 tone frames, 5 silence, 1 DC; one stream).
        /// </summary>
        [Fact]
        public void EncoderMatchesGoReferenceByteExact()
        {
            byte[] b = MlowKats.ReadBytes("go_encoder_frames.bin");
            int off = 0;
            int count = BitConverter.ToInt32(b, off);
            off += 4;
            var enc = new MlowEncoder();
            var pcm = new float[FrameSamples];
            int mismatches = 0, firstMismatch = -1;
            for (int f = 0; f < count; f++)
            {
                for (int i = 0; i < FrameSamples; i++)
                {
                    pcm[i] = BitConverter.ToSingle(b, off);
                    off += 4;
                }

                int len = BitConverter.ToInt32(b, off);
                off += 4;
                byte[] got = enc.Encode(pcm);
                if (got == null || !got.AsSpan().SequenceEqual(b.AsSpan(off, len)))
                {
                    mismatches++;
                    if (firstMismatch < 0)
                    {
                        firstMismatch = f;
                    }
                }

                off += len;
            }

            Assert.Equal(b.Length, off);
            Assert.True(mismatches == 0, mismatches + "/" + count + " frames differ from Go, first at " + firstMismatch);
        }

        [Fact]
        public void SilenceFrameEncodesAndDecodesTo960Samples()
        {
            byte[] frame = new MlowEncoder().Encode(new float[FrameSamples]);
            Assert.NotNull(frame);
            Assert.True(frame.Length > 1);
            float[] pcm = new MlowDecoder().Decode(frame);
            Assert.NotNull(pcm);
            Assert.Equal(FrameSamples, pcm.Length);
        }

        [Fact]
        public void FiftyConsecutiveSilenceFramesEncode()
        {
            var enc = new MlowEncoder();
            var dec = new MlowDecoder();
            var silence = new float[FrameSamples];
            for (int i = 0; i < 50; i++)
            {
                byte[] frame = enc.Encode(silence);
                Assert.True(frame != null, "frame " + i);
                Assert.Equal(FrameSamples, dec.Decode(frame).Length);
            }
        }

        [Fact]
        public void EncodeRejectsBadArguments()
        {
            var enc = new MlowEncoder();
            Assert.ThrowsAny<ArgumentException>(() => enc.Encode(null!));
            Assert.ThrowsAny<ArgumentException>(() => enc.Encode(new float[FrameSamples - 1]));
        }

        [Fact]
        public void EncodeSanitizesNaNAndOutOfRange()
        {
            var pcm = new float[FrameSamples];
            for (int i = 0; i < pcm.Length; i++)
            {
                pcm[i] = i % 3 == 0 ? float.NaN : (i % 3 == 1 ? 4.0f : -4.0f);
            }

            Assert.NotNull(new MlowEncoder().Encode(pcm));
        }

        [Fact]
        public void CodecImplementsMediaSeams()
        {
            IAudioFrameEncoder enc = new MlowEncoder();
            IAudioFrameDecoder dec = new MlowDecoder();
            float[] pcm = dec.Decode(enc.Encode(new float[AudioFrameFormat.FrameSamples]));
            Assert.Equal(AudioFrameFormat.FrameSamples, pcm.Length);
        }

        [Fact]
        public void OperatingPointChangePreservesDecoderState()
        {
            var pcm = new float[FrameSamples];
            for (int i = 0; i < pcm.Length; i++)
            {
                pcm[i] = 0.05f;
            }

            byte[] encoded = new MlowEncoder().Encode(pcm);
            Assert.NotNull(encoded);
            var decoder = new MlowDecoder();
            decoder.Decode(encoded);
            object highRateState = decoder.State;
            var lowRate = (byte[])encoded.Clone();
            lowRate[0] |= 0x04;
            Assert.Equal(FrameSamples, decoder.Decode(lowRate).Length);
            Assert.Same(highRateState, decoder.State);
        }

        [Fact]
        public void EncodeTimePerFrameOnMicClip()
        {
            byte[] raw = MlowKats.ReadBytes("mic_clip.raw");
            int frames = raw.Length / 2 / FrameSamples;
            var pcm = new float[frames][];
            for (int f = 0; f < frames; f++)
            {
                pcm[f] = new float[FrameSamples];
                for (int i = 0; i < FrameSamples; i++)
                {
                    pcm[f][i] = BitConverter.ToInt16(raw, 2 * (f * FrameSamples + i)) / 32768.0f;
                }
            }

            var warm = new MlowEncoder();
            for (int f = 0; f < Math.Min(frames, 5); f++)
            {
                warm.Encode(pcm[f]);
            }

            var enc = new MlowEncoder();
            var sw = Stopwatch.StartNew();
            for (int f = 0; f < frames; f++)
            {
                Assert.NotNull(enc.Encode(pcm[f]));
            }

            sw.Stop();
            double perFrame = sw.Elapsed.TotalMilliseconds / frames;
            _out.WriteLine("encoded " + frames + " frames, " + perFrame.ToString("F3") + " ms per 60 ms frame");
        }
    }
}
