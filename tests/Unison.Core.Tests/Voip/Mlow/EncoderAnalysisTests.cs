using System;
using System.Collections.Generic;
using Unison.Voip.Engine.Mlow;

namespace Unison.Core.Tests.Voip.Mlow
{
    public class EncoderAnalysisTests
    {
        [Fact]
        public void A2NlsfMatchesC()
        {
            int n = 0;
            foreach (var r in MlowKats.LoadJson("lsf_quant_io.json").EnumerateArray())
            {
                float[] got = SmplLpc.SmplA2Nlsf16(r.GetProperty("A").Floats());
                float[] want = r.GetProperty("lsf").Floats();
                for (int k = 0; k < 16; k++)
                {
                    float d = Math.Abs(got[k] - want[k]);
                    Assert.True(d < 1e-4f, "rec " + n + " nlsf[" + k + "] got " + got[k] + " want " + want[k]);
                }

                n++;
            }

            Assert.True(n > 0);
        }

        [Fact]
        public void FrontEndAMatchesC()
        {
            var recs = MlowKats.LoadJson("fe_dump.json");
            Assert.True(recs.GetArrayLength() >= 12);
            float worstWin = 0;
            int n = 0;
            foreach (var r in recs.EnumerateArray())
            {
                float[] buf = r.GetProperty("lpcbuf").Floats();
                Assert.Equal(SmplLpc.LpcBufLen, buf.Length);
                float[] want = r.GetProperty("windowed").Floats();
                int numFrame = r.GetProperty("numframe").GetInt32();
                float[] win = SmplLpc.SmplWindowLpc20(buf, numFrame < 2);
                for (int k = 0; k < SmplLpc.LpcBufLen; k++)
                {
                    worstWin = Math.Max(worstWin, Math.Abs(win[k] - want[k]));
                }

                var a = new float[SmplLpc.Order + 1];
                var f2 = new float[SmplLpc.FLen];
                SmplLpc.SmplLpcAnalyzeWithF2(win, a, f2);
                float[] wantA = r.GetProperty("A").Floats();
                float rd = 0;
                for (int k = 0; k <= SmplLpc.Order; k++)
                {
                    rd = Math.Max(rd, Math.Abs(a[k] - wantA[k]));
                }

                double r0 = r.GetProperty("R")[0].GetDouble();
                Assert.True(!(r0 > 1e-7 && rd >= 5e-3f), "rec " + n + " |dA|=" + rd);
                n++;
            }

            Assert.True(worstWin < 1e-6f, "windowing |dwin|=" + worstWin);
        }

        [Fact]
        public void PitchEstimatorMatchesCGroundTruth()
        {
            var recs = MlowKats.LoadJson("pitchio_ground_truth.json");
            Assert.True(recs.GetArrayLength() >= 30);
            var st = new PitchEstState();
            float maxPc = 0, maxAvg = 0, maxHarm = 0;
            int lagMism = 0, bsxMism = 0, checkedN = 0;
            foreach (var rec in recs.EnumerateArray())
            {
                bool cav = rec.GetProperty("cav").GetInt32() != 0;
                st.PrevLagblk = rec.GetProperty("prev_lagblk").GetInt32();
                st.PrevLagidx = rec.GetProperty("prev_lagidx").GetInt32();
                float[] ltp = rec.GetProperty("ltp_buf").Floats();
                Assert.Equal(SmplPitch.MaxLtpBufLen, ltp.Length);
                float[] f2 = new float[SmplLpc.FLen];
                Array.Copy(rec.GetProperty("F2").Floats(), f2, SmplLpc.FLen);
                PitchResult res = SmplPitchEnc.Estimate(st, ltp, f2, cav);
                if (!cav)
                {
                    continue;
                }

                maxPc = Math.Max(maxPc, Math.Abs(res.Pitchcorr - rec.GetProperty("pitchcorr").GetSingle()));
                maxAvg = Math.Max(maxAvg, Math.Abs(res.AvgLag - rec.GetProperty("avg_lag").GetSingle()));
                maxHarm = Math.Max(maxHarm, Math.Abs(res.HarmStrength - rec.GetProperty("harm").GetSingle()));
                int[] want = rec.GetProperty("laginds").Ints();
                for (int sf = 0; sf < SmplPitch.NumSubframes; sf++)
                {
                    if (res.Laginds[sf] != want[sf])
                    {
                        lagMism++;
                        break;
                    }
                }

                if (res.BlocksegIdx != rec.GetProperty("blockseg_idx").GetInt32())
                {
                    bsxMism++;
                }

                checkedN++;
            }

            Assert.True(checkedN >= 20, "checked " + checkedN);
            Assert.True(maxPc < 1e-3f, "pitchcorr " + maxPc);
            Assert.True(maxAvg < 1e-3f, "avg_lag " + maxAvg);
            Assert.Equal(0, lagMism);
            Assert.Equal(0, bsxMism);
            Assert.True(maxHarm < 0.05f, "harm " + maxHarm);
        }

        [Fact]
        public void VadMatchesCGroundTruth()
        {
            byte[] raw = MlowKats.ReadBytes("mic_clip.raw");
            var samples = new short[raw.Length / 2];
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = BitConverter.ToInt16(raw, 2 * i);
            }

            var results = new List<KeyValuePair<float, int>>();
            var vad = new SmplVadState();
            var pkt = new short[960];
            for (int off = 0; off + 960 <= samples.Length; off += 960)
            {
                Array.Copy(samples, off, pkt, 0, 960);
                VadPacketResult r = vad.ProcessPacket(pkt, 320);
                for (int f = 0; f < 3; f++)
                {
                    results.Add(new KeyValuePair<float, int>(r.VadResults[f], r.CodedAsActiveVoice ? 1 : 0));
                }
            }

            foreach (var rec in MlowKats.LoadJson("vad_ground_truth.json").EnumerateArray())
            {
                int p = rec.GetProperty("pkt").GetInt32();
                int f = rec.GetProperty("frame").GetInt32();
                var got = results[p * 3 + f];
                float d = got.Key - rec.GetProperty("spact").GetSingle();
                Assert.True(d <= 1e-4f && d >= -1e-4f, "pkt " + p + " frame " + f + " spact " + got.Key);
                Assert.True(got.Value == rec.GetProperty("cav").GetInt32(), "pkt " + p + " frame " + f + " cav");
            }
        }

        [Fact]
        public void PercFftRoundtrip()
        {
            int n = SmplPerc.PercwNfft;
            var x = new float[n];
            uint s = 12345;
            for (int i = 0; i < n; i++)
            {
                s = unchecked(s * 196314165u + 907633515u);
                x[i] = (float)(s >> 9) / (float)(1u << 23) - 1.0f;
            }

            var f = new float[n];
            MlowFft.RfftForwardOrdered(x, f);
            var back = new float[n];
            SmplPerc.RfftBackwardOrdered(f, back);
            for (int i = 0; i < n; i++)
            {
                float expected = x[i] * n;
                Assert.True(Math.Abs(back[i] - expected) < 1e-1 * (1.0 + Math.Abs(expected)), "idx " + i);
            }
        }

        [Fact]
        public void PercModelSmoke()
        {
            float[] r = SmplPerc.SmplPercModel(new PercModelState(), new float[320], 320, 20, 0, SmplPerc.SmplMaxLResp);
            Assert.Equal(SmplPerc.SmplMaxLResp, r.Length);
            Assert.All(r, v => Assert.True(Math.Abs(v) < 1e-6f));

            var dc = new float[320];
            for (int i = 0; i < dc.Length; i++)
            {
                dc[i] = 1.0f;
            }

            float[] r2 = SmplPerc.SmplPercModel(new PercModelState(), dc, 320, 20, 0, SmplPerc.SmplMaxLResp);
            Assert.True(r2[0] > 0.0f);
            float[] a = SmplPerc.SmplPercAc2a(r2, SmplPerc.SmplMaxLResp, SmplPerc.SmplPercEmphV[0], SmplPerc.SmplPercRespLen, SmplPerc.SmplPercReg);
            Assert.Equal(SmplPerc.SmplPercRespLen, a.Length);
            Assert.Equal(1.0f, a[0]);
        }

        [Fact]
        public void BitrateControllerActiveUnvoicedBudget()
        {
            var bc = new BitrateController();
            var enc = new BitrateControllerInputs
            {
                InternalSampleRate = 16000,
                PayloadSizeMs = 60,
                FecBitRate = 0,
                MainBitRate = 20000,
                Complexity = 8,
                SubFrameImportanceFactor = 1.0f,
            };
            var mp = new short[2];
            var imp = new float[2];
            bc.Control(enc, 0, 1, 0.9961f, 0.2f, -0.18f, 0, 3.0e-5f, 5.0e-5f, 0, 320, 80, mp, imp);
            Assert.Equal(23, mp[1]);
        }

        private static float[] SineVec(int n, float f, float a)
        {
            var v = new float[n];
            for (int i = 0; i < n; i++)
            {
                v[i] = (float)Math.Sin((float)i * f) * a;
            }

            return v;
        }

        private static float[] Predcoef(float a1)
        {
            var p = new float[17];
            p[0] = 1.0f;
            p[1] = a1;
            return p;
        }

        private static short[] Surv(short v)
        {
            var s = new short[SmplPerc.SmplMaxPulsesPerSf];
            for (int i = 0; i < s.Length; i++)
            {
                s[i] = v;
            }

            return s;
        }

        [Fact]
        public void CelpEncodeUnvoicedRuns()
        {
            var enc = new CelpEncoder(false, 32, 80, 4);
            var resp = new float[32];
            resp[0] = 1.0f;
            CelpSubframeOut o = enc.EncodeSubframe(SineVec(80, 0.3f, 0.1f), Predcoef(-0.5f), resp, new float[3], new[] { 1.0f, 1.0f }, new short[] { 8, 8 }, Surv(1));
            Assert.Equal(-1, o.AcbIdx[SmplPerc.SmplCelpIdxMain]);
            Assert.Equal(80, o.ExcLpc.Length);
            Assert.True(o.NPulses[SmplPerc.SmplCelpIdxMain] >= 0);
        }

        [Fact]
        public void CelpEncodeVoicedRuns()
        {
            var enc = new CelpEncoder(false, 32, 80, 4);
            for (int i = 0; i < enc.AcbState.Length; i++)
            {
                enc.AcbState[i] = (float)Math.Sin((float)i * 0.2f);
            }

            var resp = new float[32];
            resp[0] = 1.0f;
            CelpSubframeOut o = enc.EncodeSubframe(SineVec(80, 0.25f, 0.2f), Predcoef(-0.4f), resp, new[] { 60f, 60f, 60f }, new[] { 1.0f, 1.0f }, new short[] { 6, 6 }, Surv(2));
            Assert.True(o.AcbIdx[SmplPerc.SmplCelpIdxMain] >= 0);
            Assert.Equal(80, o.ExcLpc.Length);
        }

        [Fact]
        public void CelpEncodeVoicedFractionalGreedy()
        {
            var enc = new CelpEncoder(false, 32, 80, 4);
            for (int i = 0; i < enc.AcbState.Length; i++)
            {
                enc.AcbState[i] = (float)Math.Sin((float)i * 0.17f);
            }

            var resp = new float[32];
            resp[0] = 1.0f;
            CelpSubframeOut o = enc.EncodeSubframe(SineVec(80, 0.25f, 0.2f), Predcoef(-0.4f), resp, new[] { 55.5f, 55.5f, 55.5f }, new[] { 1.0f, 1.0f }, new short[] { 4, 4 }, Surv(1));
            Assert.True(o.AcbIdx[SmplPerc.SmplCelpIdxMain] >= 0);
            Assert.Equal(80, o.ExcLpc.Length);
        }
    }
}
