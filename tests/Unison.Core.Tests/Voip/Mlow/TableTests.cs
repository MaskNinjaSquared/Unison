using System;
using System.Text.Json;
using Unison.Voip.Engine.Mlow;

namespace Unison.Core.Tests.Voip.Mlow
{
    public class TableTests
    {
        [Fact]
        public void LoadSmplMemBuildsGroupDWindow()
        {
            SmplMem m = SmplMem.Load();
            Assert.Equal(6, m.RegionCount);
            Assert.Equal(0u, m.GCC);
            Assert.Equal(0u, m.GNrg);
            Assert.Equal(12178296u, m.GPitch);
            Assert.Equal(12188072u, m.GClk);
        }

        [Fact]
        public void SmplMemAccessors()
        {
            SmplMem m = SmplMem.Load();
            uint addr = SmplMem.MemPcfg + 0x1d38;
            Assert.Equal((ushort)m.U32(addr), m.U16(addr));
            Assert.Equal((short)m.U16(addr), m.I16(addr));
            Assert.Equal((int)m.U32(addr), m.I32(addr));
            ushort[] cdf = m.CdfAt(addr, 4);
            Assert.Equal(4, cdf.Length);
            for (int i = 0; i < 4; i++)
            {
                Assert.Equal(m.U16(addr + (uint)i * 2), cdf[i]);
            }

            Assert.Equal(0, m.U8(0));
            Assert.Equal(0, m.U16(0));
            Assert.Equal(0u, m.U32(0));
        }

        [Fact]
        public void SilkCosTab()
        {
            int[] t = SmplMem.SilkLsfCosTabFixQ12;
            Assert.Equal(129, t.Length);
            Assert.Equal(8192, t[0]);
            Assert.Equal(-8192, t[128]);
            Assert.Equal(0, t[64]);
            for (int i = 0; i <= 64; i++)
            {
                Assert.Equal(t[i], -t[128 - i]);
            }
        }

        [Fact]
        public void LoadSmplTablesMatchesJsonCapture()
        {
            var want = MlowKats.LoadJson("smpl_tables.json");
            SmplTables got = SmplLsf.LoadSmplTables();

            var sel = want.GetProperty("lsf_sel");
            Assert.Equal(sel.GetArrayLength(), got.LsfSel.Length);
            int i = 0;
            foreach (var row in sel.EnumerateArray())
            {
                Assert.Equal(row.UShorts(), got.LsfSel[i++]);
            }

            var grid = want.GetProperty("lsf_grid");
            Assert.Equal(grid.GetProperty("match1").UShorts(), got.LsfGrid.Match1);
            Assert.Equal(grid.GetProperty("match1_alt").UShorts(), got.LsfGrid.Match1Alt);
            Assert.Equal(grid.GetProperty("match0").UShorts(), got.LsfGrid.Match0);
            Assert.Equal(grid.GetProperty("match0_alt").UShorts(), got.LsfGrid.Match0Alt);
            Assert.Equal(want.GetProperty("lsf_extra").UShorts(), got.LsfExtra);

            var st2 = want.GetProperty("lsf_stage2");
            Assert.Equal(st2.GetArrayLength(), got.LsfStage2.Length);
            int a = 0;
            foreach (var s1 in st2.EnumerateArray())
            {
                Assert.Equal(s1.GetArrayLength(), got.LsfStage2[a].Length);
                int b = 0;
                foreach (var s2 in s1.EnumerateArray())
                {
                    Assert.Equal(s2.GetArrayLength(), got.LsfStage2[a][b].Length);
                    int c = 0;
                    foreach (var s3 in s2.EnumerateArray())
                    {
                        Assert.Equal(s3.GetArrayLength(), got.LsfStage2[a][b][c].Length);
                        int k = 0;
                        foreach (var cdf in s3.EnumerateArray())
                        {
                            Assert.Equal(cdf.UShorts(), got.LsfStage2[a][b][c][k++]);
                        }

                        c++;
                    }

                    b++;
                }

                a++;
            }
        }

        [Fact]
        public void LsfSeedGoldenChecksums()
        {
            LsfBuilt b = LsfSeed.LoadLsfBuilt();
            St1Tables st1v = b.Cb.St1[1];
            Assert.Equal(0x3d93b440u, BitConverter.SingleToUInt32Bits(st1v.Cbhalf[0][0]));
            Assert.Equal(0x3e0ff885u, BitConverter.SingleToUInt32Bits(st1v.We[0][0][0]));
            Assert.Equal(0x4062b10fu, BitConverter.SingleToUInt32Bits(st1v.Wie[0][0][0]));
            Assert.Equal(0x40883c1du, BitConverter.SingleToUInt32Bits(st1v.Bits[0]));
            Assert.Equal(6, b.Cb.St2[1][0][0].NumQlvls[0]);
            Assert.Equal(0xbf2c0e76u, BitConverter.SingleToUInt32Bits(b.Cb.St2[1][0][0].Qlvls[0][0]));
            Assert.Equal(new ushort[] { 0, 33, 140, 932, 6942, 28552, 32763 }, b.Tables.LsfStage2[1][0][0][0]);
            Assert.Equal(6, b.Synth.Valtables[1][0][0][0].Length);
        }

        [Fact]
        public void LsfSeedMatchesSharedLoaders()
        {
            LsfBuilt b = LsfSeed.LoadLsfBuilt();
            Assert.Same(b.Cb, LsfQuant.LoadLsfCb());
            Assert.Same(b.Tables, SmplLsf.LoadSmplTables());
            Assert.Same(b.Synth, SmplSynth.LoadSmplSynthTables());
            Assert.Equal(16, b.Synth.Centroids[0].Length);
            Assert.Equal(16, b.Synth.Centroids[1].Length);
            Assert.Equal(b.Cb.MinDistV, b.Synth.MinSpacing[0]);
            Assert.Equal(b.Cb.MinDistUV, b.Synth.MinSpacing[1]);
            Assert.Equal(b.Cb.MeanV, b.Synth.Grid16W[0]);
            Assert.Equal(b.Cb.MeanUV, b.Synth.Grid16W[1]);
            Assert.Equal(b.Cb.RegCond, b.Synth.Grid16Alpha);
            for (int v = 0; v < 2; v++)
            {
                for (int c = 0; c < 16; c++)
                {
                    Assert.Equal(b.Cb.St1[v].Cbhalf[c], b.Synth.Centroids[v][c]);
                }
            }
        }

        [Fact]
        public void DecodeSmplLsfMatchesVectors()
        {
            var recs = MlowKats.LoadJson("lsf_vectors.json");
            Assert.True(recs.GetArrayLength() > 0);
            SmplTables tbl = SmplLsf.LoadSmplTables();
            int i = 0;
            foreach (var rec in recs.EnumerateArray())
            {
                byte[] frame = MlowKats.Hex(rec.GetProperty("frame").GetString()!);
                var st = new SmplLsfState();
                var dec = new RangeDecoder(frame, 1, frame.Length - 1);
                SmplLsfIndices idx = SmplLsf.DecodeSmplLsf(dec, tbl, st, 0, 0);
                Assert.True(idx.Stage1 == rec.GetProperty("stage1").GetInt32(), "rec " + i + " stage1");
                Assert.True(idx.Grid == rec.GetProperty("grid").GetInt32(), "rec " + i + " grid");
                Assert.True(idx.Extra == rec.GetProperty("extra").GetInt32(), "rec " + i + " extra");
                Assert.Equal(rec.GetProperty("stage2").Ints(), idx.Stage2);
                Assert.Equal(0, dec.Err);
                i++;
            }
        }

        [Fact]
        public void DecodeSmplPulsesMatchesVectors()
        {
            var recs = MlowKats.LoadJson("pulse_vectors.json");
            Assert.True(recs.GetArrayLength() > 0);
            SmplTables tbl = SmplLsf.LoadSmplTables();
            SmplMem mem = SmplMem.Load();
            int i = 0;
            foreach (var rec in recs.EnumerateArray())
            {
                byte[] frame = MlowKats.Hex(rec.GetProperty("frame").GetString()!);
                var st = new SmplLsfState();
                var dec = new RangeDecoder(frame, 1, frame.Length - 1);
                SmplLsfIndices lsf = SmplLsf.DecodeSmplLsf(dec, tbl, st, 0, 0);
                SmplPulseResult pr = SmplPulse.DecodeSmplPulses(dec, mem, 320, 4, 1, 0, lsf.Stage1);
                Assert.Equal(rec.GetProperty("subfr").Ints(), pr.Subfr);
                var want = new int[pr.Pulses.Length];
                foreach (var pu in rec.GetProperty("pulses").EnumerateArray())
                {
                    want[pu.GetProperty("pos").GetInt32()] = pu.GetProperty("val").GetInt32();
                }

                Assert.True(want.AsSpan().SequenceEqual(pr.Pulses), "rec " + i + " pulse vector mismatch");
                Assert.Equal(0, dec.Err);
                i++;
            }
        }

        [Fact]
        public void DecodeLowRatePulseSplitUsesTwoSubframes()
        {
            const int total = 10;
            const int first = 4;
            CcTables cc = CcTables.Load();
            ushort[] cdf = CcTables.CdfWindow(cc.SplitCmf(total)!, 0, total + 2);
            var enc = new RangeEncoder(32);
            enc.EncodeCdf(first, cdf);
            enc.Done();
            Assert.Equal(0, enc.Err);

            var dec = new RangeDecoder(enc.Bytes());
            Assert.True(SmplPulse.DecodePulseSubframeCounts(dec, cc, total, 16, 2, out int[] got));
            Assert.Equal(first, got[0]);
            Assert.Equal(total - first, got[1]);
        }

        [Fact]
        public void DecodeSmplGainsMatchesVectors()
        {
            var recs = MlowKats.LoadJson("gains_vectors.json");
            Assert.True(recs.GetArrayLength() > 0);
            SmplTables tbl = SmplLsf.LoadSmplTables();
            SmplMem mem = SmplMem.Load();
            foreach (var rec in recs.EnumerateArray())
            {
                byte[] frame = MlowKats.Hex(rec.GetProperty("frame").GetString()!);
                var st = new SmplLsfState();
                var dec = new RangeDecoder(frame, 1, frame.Length - 1);
                SmplLsfIndices lsf = SmplLsf.DecodeSmplLsf(dec, tbl, st, 0, 0);
                SmplPulseResult pulses = SmplPulse.DecodeSmplPulses(dec, mem, 320, 4, 1, 0, lsf.Stage1);
                SmplGainResult g = SmplGains.DecodeSmplGains(dec, mem, 4, pulses.Subfr);
                Assert.Equal(rec.GetProperty("gain_q").Ints(), g.GainQ);
                Assert.Equal(rec.GetProperty("nrg_res").Ints(), g.NrgRes);
            }
        }

        [Fact]
        public void DecodeSmplGainsTwoSubframes()
        {
            ushort[] gain2 = CcTables.CcDcmf(new byte[]
            {
                139, 194, 196, 200, 209, 225, 239, 243, 246, 246, 253, 255,
                253, 249, 244, 239, 233, 231, 230, 228, 219, 216, 214, 214,
                213, 211, 214, 215, 220, 223, 230, 234, 237, 240, 242, 244,
                245, 245, 246, 242, 237, 230, 220, 210, 190, 170, 148, 125,
                104, 83, 64, 49, 36, 22, 14, 8, 8, 8, 8, 8, 8, 8, 8, 8,
                8, 8, 8, 8, 8, 8, 8,
            });
            ushort[] shape2 = CcTables.CcDcmf(new byte[]
            {
                47, 52, 137, 58, 61, 21, 73, 240, 55, 95, 255,
                68, 83, 108, 48, 50, 49, 45, 35, 155, 31, 41,
            });

            var enc = new RangeEncoder(16);
            enc.EncodeCdf(30, gain2);
            enc.EncodeCdf(10, shape2);
            enc.Done();
            var dec = new RangeDecoder(enc.Bytes());
            SmplGainResult got = SmplGains.DecodeSmplGains(dec, SmplMem.Load(), 2, new int[4]);
            Assert.Equal(30, got.GainMain);
            Assert.Equal(10, got.GainDelta);
            Assert.Equal(new[] { -800294, -794406, 0, 0 }, got.GainQ);
        }

        [Fact]
        public void DecodeSmplPitchMatchesVectors()
        {
            var recs = MlowKats.LoadJson("pitch_vectors.json");
            Assert.True(recs.GetArrayLength() > 0);
            SmplTables tbl = SmplLsf.LoadSmplTables();
            SmplMem mem = SmplMem.Load();
            int i = 0;
            foreach (var rec in recs.EnumerateArray())
            {
                byte[] frame = MlowKats.Hex(rec.GetProperty("frame").GetString()!);
                var st = new SmplLsfState();
                var dec = new RangeDecoder(frame, 1, frame.Length - 1);
                SmplLsfIndices lsf = SmplLsf.DecodeSmplLsf(dec, tbl, st, 0, 0);
                SmplPulseResult pulses = SmplPulse.DecodeSmplPulses(dec, mem, 320, 4, 1, 0, lsf.Stage1);
                SmplPitchResult pr = SmplPitch.DecodeSmplPitch(dec, mem, st, 320, 4, 0, pulses.Subfr);
                Assert.True(pr.Lag == rec.GetProperty("lag").GetInt32(), "rec " + i + " lag");
                Assert.True(pr.Contour == rec.GetProperty("contour").GetInt32(), "rec " + i + " contour");
                Assert.Equal(rec.GetProperty("gain_idx").Ints(), pr.GainIdx);
                Assert.Equal(rec.GetProperty("filt_idx").Ints(), pr.FiltIdx);
                Assert.Equal(rec.GetProperty("int_lag_q6").Ints(), pr.IntLagQ6);
                Assert.Equal(0, dec.Err);
                i++;
            }
        }

        [Fact]
        public void LoadPitchTablesExpandsSeed()
        {
            PitchTables t = SmplPitch.LoadPitchTables();
            Assert.Equal(217, t.Blocksegs.Length);
            Assert.Equal(187, t.Blocktracks.Length);
            Assert.Equal(218, t.BlocksegIdxCmf.Length);
            Assert.Equal(3, t.DeltaLagCmfs.Length);
            Assert.Equal(9, t.BlockTransitionCmf.Length);
        }

        private static JsonElement QuantRecords()
        {
            var recs = MlowKats.LoadJson("lsf_quant_io.json");
            Assert.True(recs.GetArrayLength() >= 12);
            return recs;
        }

        private static LsfQuantResult Quantize(JsonElement r)
        {
            float[] a = r.GetProperty("A").Floats();
            float[] lsf = r.GetProperty("lsf").Floats();
            int voiced = r.GetProperty("voiced").GetInt32();
            int lowRate = r.GetProperty("lowRate").GetInt32();
            int surv = r.GetProperty("surv").GetInt32();
            float rdw = r.GetProperty("RDw_adj").GetSingle();
            if (r.GetProperty("cond_coding").GetInt32() != 0)
            {
                return LsfQuant.LsfQuantCond(a, lsf, r.GetProperty("prev_lsf").Floats(), voiced, lowRate, rdw, surv);
            }

            return LsfQuant.Quant(a, lsf, voiced, lowRate, rdw, surv);
        }

        [Fact]
        public void LsfQuantMatchesC()
        {
            int n = 0;
            foreach (var r in QuantRecords().EnumerateArray())
            {
                LsfQuantResult res = Quantize(r);
                Assert.True(r.GetProperty("qi").Ints().AsSpan().SequenceEqual(res.Qi), "rec " + n + " qi mismatch");
                float[] q = r.GetProperty("qlsf").Floats();
                for (int k = 0; k < 16; k++)
                {
                    float d = res.QLsf[k] - q[k];
                    Assert.True(d <= 1e-4f && d >= -1e-4f, "rec " + n + " qlsf[" + k + "]");
                }

                n++;
            }
        }

        [Fact]
        public void DecoderReconstructsCQlsf()
        {
            SmplSynthTables st = SmplSynth.LoadSmplSynthTables();
            float[]? prevNlsf = null;
            float worst = 0;
            int n = 0;
            foreach (var r in QuantRecords().EnumerateArray())
            {
                LsfQuantResult res = Quantize(r);
                int grid = res.Qi[0];
                var stage2 = new int[16];
                Array.Copy(res.Qi, 1, stage2, 0, 16);
                float[] rec = SmplSynth.SmplReconstructNLSF(st, r.GetProperty("voiced").GetInt32(), 0, grid, stage2, prevNlsf);
                float[] q = r.GetProperty("qlsf").Floats();
                float rd = 0;
                for (int k = 0; k < 16; k++)
                {
                    float d = Math.Abs(rec[k] - q[k]);
                    if (d > rd)
                    {
                        rd = d;
                    }
                }

                Assert.True(n == 3 || rd < 1e-3f, "rec " + n + " reconstruct vs qlsf " + rd);
                if (rd > worst)
                {
                    worst = rd;
                }

                prevNlsf = rec;
                n++;
            }

            Assert.True(worst < 2e-3f, "worst " + worst);
        }
    }
}
