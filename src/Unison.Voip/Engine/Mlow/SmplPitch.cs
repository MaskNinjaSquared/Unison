// =============================================================================
// SmplPitch
//
// Pitch / LTP parameters. Decode side reads the LTP gains (CcTables) and the
// pitch lag contour (Group-D SmplMem window); the wire lag encoder and the
// estimator state/table types are shared with the encoder.
// Ports: meowcaller mlow/pitch.go (MIT)
// =============================================================================
using System;

namespace Unison.Voip.Engine.Mlow
{
    /// <summary>Decoded LTP/pitch parameters for one internal frame.</summary>
    internal sealed class SmplPitchResult
    {
        public readonly int[] GainIdx = new int[4];
        public readonly int[] FiltIdx = { -1, -1, -1, -1 };
        public int Lag;
        public int Contour;
        public readonly int[] SampleLagQ6 = new int[8];
        public int NumSeg;
        public readonly int[] IntLagQ6 = new int[4];
        public readonly int[] BlockLags = new int[8];
        public int NumSubfr;
    }

    /// <summary>Per-stream estimator state (cross-frame lag-block predictor).</summary>
    internal sealed class PitchEstState
    {
        public float PrevLag;
        public float PrevPitchCorr;
        public int PrevLagblk;
        public int PrevLagidx;

        /// <summary>smpl_pitch_reset_cond: after the last frame of a packet and after any unvoiced frame.</summary>
        public void ResetCond()
        {
            PrevLagblk = -1;
            PrevLagidx = -1;
        }
    }

    /// <summary>Pitch estimator result for one internal frame.</summary>
    internal sealed class PitchResult
    {
        public float Pitchcorr;
        public readonly float[] Lags = new float[SmplPitch.NumSubframes];
        public readonly int[] Laginds = new int[SmplPitch.NumSubframes];
        public float AvgLag;
        public float HarmStrength;
        public int BlocksegIdx;
    }

    internal sealed class PitchBlockSeg
    {
        public int Nblocks;
        public int[] Blocks;
        public int[] Seglens;
    }

    internal sealed class PitchBlockTrack
    {
        public int[] Track;
        public float Meanblock;
        public float Trackdeltas;
    }

    /// <summary>The loaded constant pitch tables (smpl_pitch_tables dump).</summary>
    internal sealed class PitchTables
    {
        public PitchBlockSeg[] Blocksegs;
        public PitchBlockTrack[] Blocktracks;
        public int[] Blocksegs2Idx;
        public uint[] BlocksegIdxCmf;
        public uint[][] DeltaLagCmfs;
        public int[][] BlocksegsIx;
        public int[][] FirstblockRange;
        public uint[][] BlockTransitionCmf;
    }

    internal static class SmplPitch
    {
        /// <summary>Estimator's 8 pitch sub-blocks per 20 ms internal frame.</summary>
        public const int NumSubframes = 8;

        /// <summary>Perceptually-weighted speech buffer length the estimator reads.</summary>
        public const int MaxLtpBufLen = 659;

        public const int PitchBlocksize = 64;
        public const int PitchNumBlocks = 9;
        public const int PitchNumSubframes = NumSubframes;

        private static readonly Lazy<PitchTables> Tables = new Lazy<PitchTables>(PitchSeed.BuildPitchTablesFromSeed);

        public static PitchTables LoadPitchTables()
        {
            return Tables.Value;
        }

        /// <summary>
        /// Decodes the LTP gains and pitch lags. p3 = num subframes, p6 = config,
        /// subfrCounts = per-subframe pulse counts.
        /// </summary>
        public static SmplPitchResult DecodeSmplPitch(RangeDecoder dec, SmplMem mem, SmplLsfState st, int p2, int p3, int p6, int[] subfrCounts)
        {
            var res = new SmplPitchResult();
            CcTables cc = CcTables.Load();

            int gainAccum = 0;
            int take = p3 > 4 ? 4 : p3;
            for (int sf = 0; sf < take; sf++)
            {
                int cnt = subfrCounts[sf];
                int gi = p6 != 0
                    ? dec.DecodeCdf(cc.AcbgainRowLr(st.PrevGainIdx))
                    : dec.DecodeCdf(cc.AcbgainRow(st.PrevGainIdx));
                res.GainIdx[sf] = gi;
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
                    int fi = st.PrevFiltIdx == -1
                        ? dec.DecodeCdf(cc.FcbgainV())
                        : dec.DecodeCdf(cc.FcbgainVDelta(st.PrevFiltIdx));
                    res.FiltIdx[sf] = fi;
                    st.PrevFiltIdx = fi;
                }
            }

            int avgGain = gainAccum / p3;

            uint pcfg = mem.GClk + 0x5704;
            int numContours = (int)mem.U32(pcfg + 22240);
            uint lagCdf = mem.U32(pcfg + 22248);
            uint contourMap = mem.U32(pcfg + 22244);
            uint fracBase = mem.U32(pcfg + 22252);
            uint deltaCdf = mem.U32(pcfg + 22268);

            int lag;
            if (st.PrevLag < 0)
            {
                int cnt = numContours + 1;
                if (cnt < 0)
                {
                    cnt = 0;
                }

                lag = dec.DecodeCdf(mem.CdfAt(lagCdf, cnt));
            }
            else
            {
                int di = dec.DecodeCdf(mem.CdfAt(unchecked(deltaCdf + (uint)st.PrevLag * 20), 10));
                int lo = mem.U8(unchecked(SmplMem.MemDeltaBounds + (uint)di * 2));
                int hi = mem.U8(unchecked(SmplMem.MemDeltaBounds + (uint)di * 2 + 1));
                int rN = (hi - lo) + 2;
                if (rN < 2)
                {
                    res.Lag = -1;
                    return res;
                }

                int sym = dec.DecodeCdf(mem.CdfAt(unchecked(lagCdf + (uint)lo * 2), rN));
                lag = sym + lo;
            }

            int target = lag + 1;
            int contour = -1;
            for (int i = 0; i < 217; i++)
            {
                if (mem.U8(unchecked(contourMap + (uint)i)) == target)
                {
                    contour = i;
                    break;
                }
            }

            res.Lag = lag;
            res.Contour = contour;
            if (contour < 0 || contour >= numContours)
            {
                return res;
            }

            uint ctrBase = pcfg + (uint)contour * 0x44;
            int baseLag = mem.I32(ctrBase + 0x1d38);

            int curLag2 = baseLag;
            bool readFine = true;
            if (st.PrevLag >= 0)
            {
                int delta = baseLag - st.PrevLag;
                if (delta >= -1 && delta < 3)
                {
                    readFine = false;
                }
            }

            int subfrW = 0;
            if (readFine)
            {
                int sym = dec.Decode64FineSym();
                curLag2 = unchecked((baseLag << 6) + sym);
                st.PrevFracLag = curLag2;
                st.PrevLag = baseLag;
                int segLen0 = mem.I32(ctrBase + 0x1d58);
                for (int i = 0; i < segLen0; i++)
                {
                    if (subfrW < 4)
                    {
                        res.IntLagQ6[subfrW] = curLag2;
                    }

                    if (subfrW < 8)
                    {
                        res.BlockLags[subfrW] = curLag2;
                    }

                    subfrW++;
                }

                if (subfrW < 4)
                {
                    res.IntLagQ6[subfrW] = curLag2;
                }

                if (subfrW < 8)
                {
                    res.BlockLags[subfrW] = curLag2;
                }
            }

            int cnt2 = mem.I32(ctrBase + 0x1d78);
            int segSel = 0;
            if (avgGain >= 10007)
            {
                segSel = avgGain < 14085 ? 1 : 2;
            }

            uint fracSegBase = fracBase + (uint)segSel * 0x280;
            int l3 = st.PrevFracLag;
            int l2 = curLag2;
            int startSeg = readFine ? 1 : 0;
            res.NumSeg = cnt2;
            for (int seg = startSeg; seg < cnt2; seg++)
            {
                int segLag = mem.I32(unchecked(ctrBase + 0x1d38 + (uint)seg * 4));
                int nl2 = unchecked(((l2 << 6) - l3) + ((segLag - l2) << 6));
                uint off = unchecked(fracSegBase + (uint)(nl2 * 2) + 0xfe);
                int sym = dec.DecodeCdf(mem.CdfAt(off, 65));
                l3 = unchecked(sym + st.PrevFracLag + nl2);
                if (seg < 8)
                {
                    res.SampleLagQ6[seg] = l3;
                }

                int segLen = mem.I32(unchecked(ctrBase + 0x1d58 + (uint)seg * 4));
                for (int i = 0; i < segLen; i++)
                {
                    if (subfrW < 4)
                    {
                        res.IntLagQ6[subfrW] = l3;
                    }

                    if (subfrW < 8)
                    {
                        res.BlockLags[subfrW] = l3;
                    }

                    subfrW++;
                }

                l2 = segLag;
                st.PrevFracLag = l3;
                st.PrevLag = segLag;
            }

            res.NumSubfr = subfrW;
            return res;
        }

        /// <summary>
        /// smpl_encode_lags: writes the blockseg selector + per-40-block lag indices to the
        /// range stream (the inverse of the DecodeSmplPitch contour reconstruction).
        /// </summary>
        public static void EncodeLagsWire(PitchTables tab, RangeEncoder enc, int blocksegsIx, int[] laginds, int prevLagblk, int prevLagidx, int mode)
        {
            int ixJulia = tab.Blocksegs2Idx[blocksegsIx];
            int blocksize = PitchBlocksize;
            PitchBlockSeg pblockseg = tab.Blocksegs[blocksegsIx];

            if (prevLagblk < 0)
            {
                uint[] cmf = tab.BlocksegIdxCmf;
                enc.Encode(cmf[ixJulia - 1], cmf[ixJulia], cmf[tab.Blocksegs.Length]);
            }
            else
            {
                uint[] cmf = tab.BlockTransitionCmf[prevLagblk];
                int b0 = pblockseg.Blocks[0];
                enc.Encode(cmf[b0], cmf[b0 + 1], cmf[PitchNumBlocks]);
                int startIx = tab.FirstblockRange[b0][0];
                int cmfLen = tab.FirstblockRange[b0][1] - tab.FirstblockRange[b0][0] + 1;
                uint[] cmf2 = tab.BlocksegIdxCmf;
                int lo = ixJulia - startIx - 1;
                int hi = ixJulia - startIx;
                uint c0 = cmf2[startIx];
                enc.Encode(cmf2[startIx + lo] - c0, cmf2[startIx + hi] - c0, cmf2[startIx + cmfLen] - c0);
            }

            int blk = pblockseg.Blocks[0];
            int deltaBlk = blk - prevLagblk;
            int startSeg = 0;
            int lagindsIx = 0;
            if (!(prevLagblk > -1 && deltaBlk >= -1 && deltaBlk <= 2))
            {
                uint idxMod = unchecked((uint)(laginds[lagindsIx] - blk * blocksize));
                enc.Encode(idxMod, idxMod + 1, (uint)blocksize);
                prevLagblk = blk;
                prevLagidx = laginds[lagindsIx];
                lagindsIx += pblockseg.Seglens[0];
                startSeg = 1;
            }

            uint[] deltaLagCmf = tab.DeltaLagCmfs[mode];
            for (int k = startSeg; k < pblockseg.Nblocks; k++)
            {
                blk = pblockseg.Blocks[k];
                int idx = laginds[lagindsIx];
                lagindsIx += pblockseg.Seglens[k];
                deltaBlk = blk - prevLagblk;
                int deltaIdx = idx - prevLagidx;
                int prevLagidxMod = prevLagidx - prevLagblk * blocksize;
                int deltaRangeStart = -prevLagidxMod + deltaBlk * blocksize;
                int cmfBase = deltaRangeStart + 2 * blocksize - 1;
                int ix = deltaIdx - deltaRangeStart;
                uint p0 = deltaLagCmf[cmfBase];
                enc.Encode(deltaLagCmf[cmfBase + ix] - p0, deltaLagCmf[cmfBase + ix + 1] - p0, deltaLagCmf[cmfBase + blocksize] - p0);
                prevLagblk = blk;
                prevLagidx = idx;
            }
        }

        /// <summary>Lag predictor after the voiced lag encode: (blocks[nblocks-1], laginds[NumSubframes-1]).</summary>
        public static void SmplLagsPredictorAfter(PitchTables tab, int blocksegsIx, int[] laginds, out int prevLagblk, out int prevLagidx)
        {
            PitchBlockSeg pblockseg = tab.Blocksegs[blocksegsIx];
            prevLagblk = pblockseg.Blocks[pblockseg.Nblocks - 1];
            prevLagidx = laginds[NumSubframes - 1];
        }
    }
}
