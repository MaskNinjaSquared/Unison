// =============================================================================
// SmplEncode
//
// MLow entropy encoder: [TOC || range-coded body] from analyzed frame
// parameters, the exact inverse of the decoder's active-frame body decode
// (LSF, pulses, unvoiced gains / voiced LTP gains + lag contour), mutating
// the predictor state identically to the decoder.
// Ports: meowcaller mlow/encoder.go (MIT)
// =============================================================================
using System;
using System.Collections.Generic;

namespace Unison.Voip.Engine.Mlow
{
    /// <summary>One internal frame's LSF index set (inverse of DecodeSmplLsf).</summary>
    internal sealed class SmplLsfParams
    {
        public int Stage1;
        public int Grid;
        public readonly int[] Stage2 = new int[16];
        public int Extra;
    }

    /// <summary>One internal frame's excitation; MagRuns/SignSyms are replayed verbatim.</summary>
    internal sealed class SmplPulseParams
    {
        public int Total;
        public readonly int[] Subfr = new int[4];
        public readonly List<int> MagRuns = new List<int>();
        public readonly List<SmplRawSym> SignSyms = new List<SmplRawSym>();
    }

    /// <summary>One unvoiced internal frame's gain block (inverse of DecodeSmplGains).</summary>
    internal sealed class SmplGainParams
    {
        public int GainMain;
        public int GainDelta;
        public readonly int[] NrgRes = new int[4];
    }

    /// <summary>One voiced internal frame's LTP gains/filters + the estimator's lag contour.</summary>
    internal sealed class SmplPitchParams
    {
        public readonly int[] GainIdx = new int[4];
        public readonly int[] FiltIdx = new int[4];
        public int BlocksegIdx;
        public readonly int[] Laginds = new int[8];
    }

    /// <summary>One 20 ms internal frame's full parameter set.</summary>
    internal sealed class SmplInternalParams
    {
        public SmplLsfParams Lsf = new SmplLsfParams();
        public SmplPulseParams Pulses = new SmplPulseParams();
        public bool HasPitch;
        public SmplPitchParams Pitch = new SmplPitchParams();
        public SmplGainParams Gains = new SmplGainParams();
    }

    /// <summary>The analyzed parameter set for one 60 ms MLow frame.</summary>
    internal sealed class SmplFrameParams
    {
        public byte Toc;
        public int Config;
        public readonly SmplInternalParams[] Internal = { new SmplInternalParams(), new SmplInternalParams(), new SmplInternalParams() };
    }

    internal static class SmplEncode
    {
        /// <summary>Range-encoder body capacity (SMPL_ENCODE_BUF_BYTES).</summary>
        public const int EncodeBufBytes = 512;

        private static void EncodeSmplLsf(RangeEncoder enc, SmplTables t, SmplLsfState st, int config, int intf, SmplLsfParams lsf)
        {
            int sel = 0;
            if (intf != 0)
            {
                sel = st.PrevStage1 != 0 ? 2 : 1;
            }

            int stage1 = lsf.Stage1;
            enc.EncodeCdf(stage1, t.LsfSel[sel]);

            bool m = intf != 0 && stage1 == st.PrevStage1;
            if (!m)
            {
                st.PrevGainIdx = -1;
                st.PrevFiltIdx = -1;
                st.PrevLag = -1;
                st.PrevFracLag = -1;
                st.PrevLagblk = -1;
                st.PrevLagidx = -1;
            }

            st.PrevStage1 = stage1;

            ushort[] gridCdf;
            if (m && stage1 != 0)
            {
                gridCdf = t.LsfGrid.Match1;
            }
            else if (m)
            {
                gridCdf = t.LsfGrid.Match1Alt;
            }
            else if (stage1 != 0)
            {
                gridCdf = t.LsfGrid.Match0Alt;
            }
            else
            {
                gridCdf = t.LsfGrid.Match0;
            }

            enc.EncodeCdf(lsf.Grid, gridCdf);
            st.PrevMatch = m;
            st.HavePrev = true;

            ushort[][] st2 = t.LsfStage2[stage1][config][lsf.Grid];
            for (int k = 0; k < 16; k++)
            {
                enc.EncodeCdf(lsf.Stage2[k], st2[k]);
            }

            enc.EncodeCdf(lsf.Extra, t.LsfExtra);
        }

        /// <summary>Inverse of DecodeSmplPulses: count interval, recursive split, then the recorded magnitude/sign symbols.</summary>
        private static void EncodeSmplPulses(RangeEncoder enc, int p2, int p3, int p4, int p6, int s1, SmplPulseParams pp)
        {
            CcTables cc = CcTables.Load();
            int idx = p4 + s1;
            int bByte = SmplPulse.Mem8Static(unchecked(0xe8990u + (uint)(p6 * 3 + idx)));
            int frameLen4k = bByte * p2 / 320;
            int subfrLen16 = frameLen4k / p3;
            int total = pp.Total;

            uint l = (uint)frameLen4k;
            uint ft = TriT((uint)l, l);
            if (ft == 0)
            {
                ft = 1;
            }

            uint fl = total > 0 ? TriT(unchecked((uint)(total - 1)), l) : 0;
            uint fh = TriT((uint)total, l);
            enc.Encode(fl, fh, ft);
            if (total == 0)
            {
                return;
            }

            int finalSum = pp.Subfr[0] + pp.Subfr[1];
            int initSum = total - subfrLen16 * 2;
            if (initSum < 0)
            {
                initSum = 0;
            }

            int lo = total - 80;
            if (lo < 0)
            {
                lo = 0;
            }

            if (initSum < lo)
            {
                return;
            }

            int hiBound = total - lo;
            if (initSum < hiBound)
            {
                ushort[] cdf = CcTables.CdfWindow(cc.SplitCmf(total), initSum - lo, (hiBound - initSum) + 2);
                enc.EncodeCdf(finalSum - initSum, cdf);
            }

            if (finalSum > 0)
            {
                EncodeSplit3537(enc, cc, finalSum, subfrLen16, pp.Subfr[0]);
            }

            if (finalSum < total)
            {
                EncodeSplit3537(enc, cc, total - finalSum, subfrLen16, pp.Subfr[2]);
            }

            int posPer = p2 / p3;
            int magIdx = 0;
            for (int subfr = 0; subfr < p3; subfr++)
            {
                int cnt = pp.Subfr[subfr];
                if (cnt <= 0)
                {
                    continue;
                }

                int pos = posPer;
                int c = cnt;
                for (int k = 0; k < cnt; k++)
                {
                    int oct = (pos + 7) / 8;
                    RunlenCmfs bucket = cc.Runlen(oct);
                    int start = bucket.MaxSamples - pos;
                    int m = pp.MagRuns[magIdx];
                    magIdx++;
                    enc.EncodeCdf(m, CcTables.CdfWindow(bucket.Cmf(c), start, pos + 1));
                    if (m > 0 || k == 0)
                    {
                        pos -= m;
                    }

                    c--;
                }
            }

            foreach (SmplRawSym rs in pp.SignSyms)
            {
                enc.EncodeRawSymbol(rs.Sym, rs.Nbits);
            }
        }

        private static uint TriT(uint k, uint l)
        {
            unchecked
            {
                uint a = (k + 2) * (l + 1);
                uint b = ((k - 1) * (k + 131070)) >> 1;
                return (a - b) & 0xffff;
            }
        }

        /// <summary>Inverse of smplSplit3537: encodes the first-half count s0.</summary>
        private static void EncodeSplit3537(RangeEncoder enc, CcTables cc, int count, int granularity, int s0)
        {
            int lo = count;
            if (granularity < lo)
            {
                lo = granularity;
            }

            int minSplit = count - granularity;
            if (minSplit < 0)
            {
                minSplit = 0;
            }

            if (lo < minSplit || minSplit == lo)
            {
                return;
            }

            ushort[] cdf = CcTables.CdfWindow(cc.SplitCmf(count), minSplit, (lo - minSplit) + 2);
            enc.EncodeCdf(s0 - minSplit, cdf);
        }

        /// <summary>Inverse of DecodeSmplGains: main/delta gain, then per-subframe nrgres with the gain-derived shift.</summary>
        private static void EncodeSmplGains(RangeEncoder enc, int p3, int[] subfrCounts, SmplGainParams gp)
        {
            CcTables cc = CcTables.Load();
            enc.EncodeCdf(gp.GainMain, cc.NrgresGain(p3));
            enc.EncodeCdf(gp.GainDelta, cc.NrgresShape(p3));
            int cfgSel = p3 == 2 ? 1 : 2;

            int off6 = p3 * gp.GainDelta;
            int base7 = unchecked(gp.GainMain * cc.NrgStep(cfgSel) - 0x154000);
            var gainQ = new int[4];
            int take = p3 > 4 ? 4 : p3;
            for (int sf = 0; sf < take; sf++)
            {
                int cbv = cc.GainRecon(p3 == 4, sf + off6);
                gainQ[sf] = unchecked(base7 + (cbv << 4));
            }

            for (int sf = 0; sf < take; sf++)
            {
                int cnt = subfrCounts[sf];
                if (cnt <= 0)
                {
                    continue;
                }

                int bucket = cnt >= 30 ? 3 : (cnt & 0xffff) / 10;
                int g = unchecked(gainQ[sf] + 8192) >> 14;
                if (g < -85)
                {
                    g = -85;
                }

                int negPart = (g >> 31) & g;
                enc.EncodeCdf(gp.NrgRes[sf], cc.FcbgOffset(cfgSel, bucket, -negPart));
            }
        }

        /// <summary>
        /// Inverse of DecodeSmplPitch: LTP gains/filters, then the lag contour (blockseg selector +
        /// per-block lag indices), mutating the predictor state identically.
        /// </summary>
        internal static void EncodeSmplPitch(RangeEncoder enc, SmplMem mem, SmplLsfState st, int p2, int p3, int p6, int[] subfrCounts, SmplPitchParams pp)
        {
            CcTables cc = CcTables.Load();
            int gainAccum = 0;
            int take = p3 > 4 ? 4 : p3;
            for (int sf = 0; sf < take; sf++)
            {
                int cnt = subfrCounts[sf];
                int gi = pp.GainIdx[sf];
                if (p6 != 0)
                {
                    enc.EncodeCdf(gi, cc.AcbgainRowLr(st.PrevGainIdx));
                }
                else
                {
                    enc.EncodeCdf(gi, cc.AcbgainRow(st.PrevGainIdx));
                }

                st.PrevGainIdx = gi;
                int w0, w2;
                if (p6 != 0)
                {
                    cc.AcbgainWeightsLr(gi, out w0, out w2);
                }
                else
                {
                    cc.AcbgainWeights(gi, out w0, out w2);
                }

                gainAccum += w0 + 2 * w2;
                if (cnt > 0)
                {
                    int fi = pp.FiltIdx[sf];
                    if (st.PrevFiltIdx == -1)
                    {
                        enc.EncodeCdf(fi, cc.FcbgainV());
                    }
                    else
                    {
                        enc.EncodeCdf(fi, cc.FcbgainVDelta(st.PrevFiltIdx));
                    }

                    st.PrevFiltIdx = fi;
                }
            }

            int avgGain = gainAccum / p3;
            int mode = 0;
            if (avgGain >= 10007)
            {
                mode = avgGain < 14085 ? 1 : 2;
            }

            PitchTables tab = SmplPitch.LoadPitchTables();
            SmplPitch.EncodeLagsWire(tab, enc, pp.BlocksegIdx, pp.Laginds, st.PrevLagblk, st.PrevLagidx, mode);
            int nblk, nidx;
            SmplPitch.SmplLagsPredictorAfter(tab, pp.BlocksegIdx, pp.Laginds, out nblk, out nidx);
            st.PrevLagblk = nblk;
            st.PrevLagidx = nidx;
        }

        /// <summary>Builds [TOC || range-coded body]; null when the range-encoder buffer overflows.</summary>
        public static byte[] EncodeSmplFrame(SmplFrameParams fp)
        {
            const int p2 = 320, p3 = 4, p4 = 1;
            int p6 = fp.Config;
            SmplTables tbl = SmplLsf.LoadSmplTables();
            SmplMem mem = SmplMem.Load();
            var enc = new RangeEncoder(1 + EncodeBufBytes);
            var st = new SmplLsfState();
            for (int f = 0; f < 3; f++)
            {
                SmplInternalParams ip = fp.Internal[f];
                EncodeSmplLsf(enc, tbl, st, fp.Config, f, ip.Lsf);
                EncodeSmplPulses(enc, p2, p3, p4, p6, ip.Lsf.Stage1, ip.Pulses);
                if (ip.Lsf.Stage1 == 1)
                {
                    EncodeSmplPitch(enc, mem, st, p2, p3, p6, ip.Pulses.Subfr, ip.Pitch);
                }
                else
                {
                    EncodeSmplGains(enc, p3, ip.Pulses.Subfr, ip.Gains);
                }
            }

            enc.Done();
            if (enc.Err != 0)
            {
                return null;
            }

            int n = enc.ConsumedLen();
            var o = new byte[1 + n];
            o[0] = fp.Toc;
            Buffer.BlockCopy(enc.Bytes(), 0, o, 1, n);
            return o;
        }
    }
}
