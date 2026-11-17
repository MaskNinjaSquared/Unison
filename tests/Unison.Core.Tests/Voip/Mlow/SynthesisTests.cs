using System;
using System.Collections.Generic;
using System.Text.Json;
using Unison.Voip.Engine.Mlow;

namespace Unison.Core.Tests.Voip.Mlow
{
    public class SynthesisTests
    {
        private static NoiseGenerator ToNoiseGenerator(JsonElement j)
        {
            var ng = new NoiseGenerator
            {
                EnvSmth = j.GetProperty("env_smth").GetSingle(),
                EnvLast = j.GetProperty("env_last").GetSingle(),
                PrevVoiced = j.GetProperty("prev_voiced").GetInt32() != 0,
                SinceUnvoiced = j.GetProperty("since_unvoiced").GetInt32(),
                RandSeed = j.GetProperty("rand_seed").GetInt32(),
            };
            Array.Copy(j.GetProperty("out_state_uv").Floats(), ng.OutStateUV, 2);
            Array.Copy(j.GetProperty("out_state_v").Floats(), ng.OutStateV, 2);
            Array.Copy(j.GetProperty("corr_smth").Floats(), ng.CorrSmth, 3);
            Array.Copy(j.GetProperty("shape_state").Floats(), ng.ShapeState, 2);
            return ng;
        }

        [Fact]
        public void GenNoiseMatchesC()
        {
            var recs = MlowKats.LoadJson("gennoise_vectors.json");
            Assert.True(recs.GetArrayLength() > 0);
            var fcbgainsUV = new float[91];
            for (int ix = 0; ix <= 90; ix++)
            {
                fcbgainsUV[ix] = (float)Math.Pow(10, 0.05 * (ix - 90.0));
            }

            int vChecked = 0, uv0Checked = 0, uvpChecked = 0, n = 0;
            foreach (var r in recs.EnumerateArray())
            {
                NoiseGenerator ng = ToNoiseGenerator(r.GetProperty("ng_in"));
                NoiseGenerator ngOut = ToNoiseGenerator(r.GetProperty("ng_out"));
                int voiced = r.GetProperty("voiced").GetInt32();
                int sfPulses = r.GetProperty("sf_pulses").GetInt32();
                float[] want = r.GetProperty("noise").Floats();
                var noise = new float[160];
                SmplNoise.SmplCelpGenNoise(
                    ng,
                    r.GetProperty("exc_pre").Floats(),
                    0,
                    80,
                    voiced == 1,
                    sfPulses,
                    r.GetProperty("nrgres").GetSingle(),
                    r.GetProperty("fcbg_idx").GetInt32(),
                    r.GetProperty("lsf").Floats(),
                    r.GetProperty("norm_br").GetSingle(),
                    fcbgainsUV,
                    noise);

                Assert.True(ng.RandSeed == r.GetProperty("seed_out").GetInt32() && ng.RandSeed == ngOut.RandSeed, "rec " + n + " rand_seed");
                for (int i = 0; i < 80; i++)
                {
                    Assert.True(Math.Abs(noise[i] - want[i]) < 1e-6f, "rec " + n + " noise[" + i + "] " + noise[i] + " != " + want[i]);
                }

                Assert.True(Math.Abs(ng.EnvLast - ngOut.EnvLast) < 1e-6f, "rec " + n + " env_last");
                for (int k = 0; k < 2; k++)
                {
                    Assert.True(Math.Abs(ng.OutStateUV[k] - ngOut.OutStateUV[k]) < 1e-6f, "rec " + n + " out_state_uv");
                    Assert.True(Math.Abs(ng.OutStateV[k] - ngOut.OutStateV[k]) < 1e-6f, "rec " + n + " out_state_v");
                }

                if (voiced == 1)
                {
                    vChecked++;
                }
                else if (sfPulses == 0)
                {
                    uv0Checked++;
                }
                else
                {
                    uvpChecked++;
                }

                n++;
            }

            Assert.True(vChecked > 0 && uv0Checked > 0 && uvpChecked > 0, "vectors must exercise all paths");
        }

        [Fact]
        public void ExcPreMatchesC()
        {
            var crecs = MlowKats.LoadJson("exc_pre_lags.json");
            string[] frames = MlowKats.LoadJson("inbound_capture_frames.json").Strings();
            var cmap = new Dictionary<(int, int, int), JsonElement>();
            foreach (var c in crecs.EnumerateArray())
            {
                cmap[(c.GetProperty("packet").GetInt32(), c.GetProperty("frame").GetInt32(), c.GetProperty("sf").GetInt32())] = c;
            }

            SmplTables tbl = SmplLsf.LoadSmplTables();
            SmplSynthTables synthT = SmplSynth.LoadSmplSynthTables();
            SmplMem mem = SmplMem.Load();
            var lstate = new SmplLsfState();
            var celp = new CelpDecState();
            celp.TraceExcPre = true;
            float[]? prevNlsf = null;

            int uvOk = 0, uvBad = 0, vOk = 0, vBad = 0;
            for (int packet = 0; packet < frames.Length; packet++)
            {
                byte[] frame = MlowKats.Hex(frames[packet]);
                if (frame.Length == 0)
                {
                    continue;
                }

                SmplToc toc = SmplToc.Parse(frame[0]);
                if (toc.StdOpus || toc.Sid || !toc.Active)
                {
                    continue;
                }

                int config = (frame[0] >> 2) & 1;
                bool lowRate = ((frame[0] >> 2) & 1) != 0;
                var dec = new RangeDecoder(frame, 1, frame.Length - 1);
                for (int f = 0; f < 3; f++)
                {
                    SmplLsfIndices lsf = SmplLsf.DecodeSmplLsf(dec, tbl, lstate, config, f);
                    SmplPulseResult pulses = SmplPulse.DecodeSmplPulses(dec, mem, 320, 4, 1, config, lsf.Stage1);
                    bool voiced = lsf.Stage1 == 1;
                    var p = new CelpDecParams { Voiced = voiced };
                    Array.Copy(pulses.Subfr, p.SfPulses, 4);
                    int total = 0;
                    foreach (int c in pulses.Subfr)
                    {
                        total += c;
                    }

                    p.TotalPulses = total;
                    if (voiced)
                    {
                        SmplPitchResult pr = SmplPitch.DecodeSmplPitch(dec, mem, lstate, 320, 4, config, pulses.Subfr);
                        for (int b = 0; b < 8; b++)
                        {
                            float v = (float)pr.BlockLags[b] * 0.5f + 32.0f;
                            if (v > 320.0f)
                            {
                                v = 320.0f;
                            }

                            p.BlockLags[b] = v;
                        }

                        for (int sf = 0; sf < 4; sf++)
                        {
                            p.AcbgIdx[sf] = pr.GainIdx[sf];
                            if (pr.FiltIdx[sf] > 0)
                            {
                                p.FcbgIdx[sf] = pr.FiltIdx[sf];
                            }
                        }
                    }
                    else
                    {
                        SmplGainResult g = SmplGains.DecodeSmplGains(dec, mem, 4, pulses.Subfr);
                        Array.Copy(g.GainQ, p.NrgresDbqQ14, 4);
                        Array.Copy(g.NrgRes, p.FcbgIdx, 4);
                    }

                    float[] nlsf = SmplSynth.SmplReconstructNLSF(synthT, lsf.Stage1, config, lsf.Grid, lsf.Stage2, prevNlsf);
                    var sig = new float[320];
                    celp.SynthFrame(nlsf, lsf.Extra, pulses.Pulses, p, lowRate, 320, sig);
                    prevNlsf = nlsf;

                    for (int sf = 0; sf < 4; sf++)
                    {
                        if (!cmap.TryGetValue((packet, f, sf), out JsonElement c))
                        {
                            continue;
                        }

                        if (voiced)
                        {
                            float[] lags = c.GetProperty("lags").Floats();
                            Assert.True(p.BlockLags[2 * sf] == lags[0] && p.BlockLags[2 * sf + 1] == lags[1], "per-block lags diverge");
                        }

                        var cexc = new float[80];
                        foreach (var pair in c.GetProperty("nz").EnumerateArray())
                        {
                            cexc[(int)pair[0].GetDouble()] = (float)pair[1].GetDouble();
                        }

                        bool bad = false;
                        for (int i = 0; i < 80; i++)
                        {
                            if (Math.Abs(celp.ExcPre[sf * 80 + i] - cexc[i]) > 2e-5f)
                            {
                                bad = true;
                            }
                        }

                        if (voiced && bad) { vBad++; }
                        else if (voiced) { vOk++; }
                        else if (bad) { uvBad++; }
                        else { uvOk++; }
                    }
                }
            }

            Assert.True(uvOk + vOk > 0, "no subframes checked");
            Assert.Equal(0, uvBad);
            Assert.Equal(0, vBad);
        }

        [Fact]
        public void LowRateUvPulseShape()
        {
            var state = new float[2];
            var got = new float[] { 1, 0, 0 };
            CelpDec.SmplLowRateUvPulseShape(got, 0, got.Length, state);
            var want = new[] { 0.5f, 0.333f, 0.110889f };
            for (int i = 0; i < want.Length; i++)
            {
                Assert.True(Math.Abs(got[i] - want[i]) <= 1e-6, "sample " + i);
            }

            Assert.True(Math.Abs(state[1] - 0.110889f) <= 1e-6);
        }

        [Fact]
        public void LowRateTilt()
        {
            float state = 0.5f;
            var got = new float[] { 1, 0 };
            CelpDec.SmplLowRateTilt(got, 0, got.Length, ref state);
            var want = new[] { 0.92f, 0.16f };
            for (int i = 0; i < want.Length; i++)
            {
                Assert.True(Math.Abs(got[i] - want[i]) <= 1e-6, "sample " + i);
            }

            Assert.Equal(0f, state);
        }

        [Fact]
        public void LowRateCelpUsesTwoSubframes()
        {
            var state = new CelpDecState();
            var current = new float[16];
            var previous = new float[16];
            for (int i = 0; i < 16; i++)
            {
                previous[i] = 0.1f + i * 0.14f;
                current[i] = previous[i] + 0.02f;
                state.LsfPrev[i] = previous[i];
            }

            var output = new float[320];
            state.SynthFrame(current, 1, new int[320], new CelpDecParams(), true, 320, output);
            for (int i = 0; i < 16; i++)
            {
                float want = previous[i] * 0.05f + current[i] * 0.95f;
                Assert.True(Math.Abs(state.LsfPrev[i] - want) <= 1e-6, "lsfPrev[" + i + "]");
            }
        }

        private static float RdF32(byte[] b, ref int o)
        {
            float v = BitConverter.ToSingle(b, o);
            o += 4;
            return v;
        }

        private static int RdI32(byte[] b, ref int o)
        {
            int v = BitConverter.ToInt32(b, o);
            o += 4;
            return v;
        }

        [Fact]
        public void HpPostfilterMatchesC()
        {
            const int frameLen = 320;
            const float i16Lsb = 1.0f / 32768.0f;
            byte[] data = MlowKats.ReadBytes("hp_postfilter_vectors.raw");
            int o = 0;
            int count = RdI32(data, ref o);
            Assert.NotEqual(0, count);
            float worst = 0;
            for (int f = 0; f < count; f++)
            {
                RdI32(data, ref o);
                RdI32(data, ref o);
                var lags = new float[8];
                for (int i = 0; i < 8; i++) { lags[i] = RdF32(data, ref o); }
                float lo1 = RdF32(data, ref o);
                float lo2 = RdF32(data, ref o);
                var hp = new float[4];
                for (int i = 0; i < 4; i++) { hp[i] = RdF32(data, ref o); }
                float lagOld = RdF32(data, ref o);
                var xOld = new float[frameLen];
                for (int i = 0; i < frameLen; i++) { xOld[i] = RdF32(data, ref o); }
                var coefMa = new float[3];
                var coefAr = new float[3];
                for (int i = 0; i < 3; i++) { coefMa[i] = RdF32(data, ref o); }
                for (int i = 0; i < 3; i++) { coefAr[i] = RdF32(data, ref o); }
                var yPre = new float[frameLen];
                for (int i = 0; i < frameLen; i++) { yPre[i] = RdF32(data, ref o); }
                var yPost = new float[frameLen];
                for (int i = 0; i < frameLen; i++) { yPost[i] = RdF32(data, ref o); }

                float lag = 0;
                if (lags[0] > 0.0f)
                {
                    float sl = 0, sll = 0;
                    foreach (float l in lags)
                    {
                        sl += l;
                        sll += l * l;
                    }

                    lag = sll / sl;
                }

                var st = new HpPostfilterState
                {
                    StateLoEmph1 = lo1,
                    StateLoEmph2 = lo2,
                    StateHp = hp,
                    LagOld = lagOld,
                    XOld = xOld,
                    CoefMA = coefMa,
                    CoefAR = coefAr,
                };
                var output = new float[frameLen];
                SmplPostfilter.SmplHpPostfilter(st, yPre, frameLen, lag, output);
                for (int i = 0; i < frameLen; i++)
                {
                    float d = Math.Abs(output[i] - yPost[i]);
                    if (d > worst)
                    {
                        worst = d;
                    }
                }
            }

            Assert.True(worst < i16Lsb, "hp_postfilter diverges by " + worst);
        }

        [Fact]
        public void HarmPostfilterMatchesC()
        {
            const float i16Lsb = 1.0f / 32768.0f;
            const float transitionTol = 6.0e-4f;
            const int totPostfiltDelay = SmplPostfilter.HarmFBDelay + SmplPostfilter.HarmDelay;
            byte[] data = MlowKats.ReadBytes("harm_postfilter_vectors.raw");
            int o = 0;
            int count = RdI32(data, ref o);
            Assert.NotEqual(0, count);
            var st = new HarmPostfilterState();
            float worst = 0, worstSteady = 0;
            for (int p = 0; p < count; p++)
            {
                RdI32(data, ref o);
                int plen = RdI32(data, ref o);
                int nlags = RdI32(data, ref o);
                float nbr = RdF32(data, ref o);
                var lags = new float[nlags];
                for (int i = 0; i < nlags; i++) { lags[i] = RdF32(data, ref o); }
                var inp = new float[plen];
                for (int i = 0; i < plen; i++) { inp[i] = RdF32(data, ref o); }
                var cout = new float[plen];
                for (int i = 0; i < plen; i++) { cout[i] = RdF32(data, ref o); }

                bool transition = lags[0] == 0.0f;
                SmplPostfilter.SmplHarmPostfilter(st, inp, plen, lags, nlags, nbr);
                for (int i = 0; i < plen; i++)
                {
                    float d = Math.Abs(inp[i] - cout[i]);
                    if (d > worst)
                    {
                        worst = d;
                    }

                    if (!(transition && i < totPostfiltDelay) && d > worstSteady)
                    {
                        worstSteady = d;
                    }
                }
            }

            Assert.True(worstSteady < i16Lsb, "steady-state diverges by " + worstSteady);
            Assert.True(worst < transitionTol, "transition residual " + worst);
        }
    }
}
