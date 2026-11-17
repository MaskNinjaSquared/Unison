// =============================================================================
// SmplPitchEnc
//
// Encoder pitch estimator (20 ms / 8-subframe config): HP-filter + 2x
// downsample of the perceptually-weighted ltp_buf, open-loop block-track
// survivor search at coarse resolution, per-block refinement at full
// resolution, then rate / previous-lag / spectral-harmonicity biased selection.
// Ports: meowcaller mlow/pitch_enc.go (MIT)
// =============================================================================
using System;
using System.Collections.Generic;

namespace Unison.Voip.Engine.Mlow
{
    /// <summary>Reusable working buffers for one SmplPitchEnc.SmplPitch call.</summary>
    internal sealed class PitchEncScratch
    {
        internal readonly float[] Stage1 = new float[SmplPitch.MaxLtpBufLen + SmplPitchEnc.DownsampDelay + 64];
        internal readonly float[] LtpBufHp = new float[SmplPitch.MaxLtpBufLen - SmplPitchEnc.LookaheadLen];
        internal readonly float[] Stage1Ds = new float[(SmplPitch.MaxLtpBufLen + SmplPitchEnc.DownsampDelay) / 2 + 8];
        internal readonly float[] E1 = new float[SmplPitchEnc.NumLagsStage1 * SmplPitch.NumSubframes + 16];
        internal readonly float[] E2 = new float[SmplPitch.NumSubframes];
        internal readonly float[] C = new float[SmplPitchEnc.CeCap];
        internal readonly float[] E = new float[SmplPitchEnc.CeCap];
        internal readonly float[] H = new float[SmplPitchEnc.NumlagsCoarse * SmplPitch.NumSubframes * 2 + 64];
        internal readonly float[] SqrtE1 = new float[SmplPitchEnc.NumLagsStage1];
        internal readonly float[] E1Fs = new float[2 * (SmplPitchEnc.NumLagsStage1 - 1) * SmplPitch.NumSubframes + 16];
        internal readonly float[] E1Ext = new float[2 * (SmplPitchEnc.NumLagsStage1 - 1) + 7 * SmplPitchEnc.LagSubfrlen];
        internal readonly float[] HComb = new float[2 * SmplPitchEnc.Pitchblock];
        internal readonly float[] SqrtE1Blk = new float[SmplPitchEnc.Pitchblock + 1];
        internal readonly float[] HarmCache = new float[SmplSignalMode.HarmCacheLen];
        internal readonly float[] Utils = new float[SmplPitch.LoadPitchTables().Blocktracks.Length];
        internal readonly bool[] Taken = new bool[SmplPitch.LoadPitchTables().Blocktracks.Length];
        internal readonly Dictionary<int, int> LagindCache = new Dictionary<int, int>();
        internal readonly List<int[]> LagindsSurv = new List<int[]>();
        internal readonly List<int> BlocksegsIxList = new List<int>();
        internal readonly List<int[]> RowPool = new List<int[]>();
    }

    internal static class SmplPitchEnc
    {
        private const int FsKhz = 16;
        private const int Stage1FsKhz = 8;
        private const int CoarseFsKhz = 16;
        private const int TotInterpDelay = 6;
        private const int MinpitchMs = 2;
        private const int MaxpitchMs = 20;
        private const int MinpitchLen = MinpitchMs * FsKhz;
        private const int MaxpitchLen = MaxpitchMs * FsKhz;
        private const int MinpitchStage1 = MinpitchMs * Stage1FsKhz - TotInterpDelay;
        private const int MaxpitchStage1 = MaxpitchMs * Stage1FsKhz + TotInterpDelay;

        private const float PitchDeltawght = 0.1439f;
        private const float PitchShortwght1 = 0.04f;
        private const float SpecHarmBias = 2.5f;
        private const float Prevwght = 0.7981f;
        private const float PrevwghtSpan = 0.15f;
        private const float RatewghtHr = 0.022f;

        internal const int LagSubfrlen = 40;
        private const int LagSubfrlenStage1 = Stage1FsKhz * LagSubfrlen / FsKhz;
        private const int PitchblockMs = 2;
        internal const int LookaheadLen = 7;

        internal const int DownsampDelay = 7;
        private const int InterpolDelayC = 4;
        internal const int Pitchblock = PitchblockMs * FsKhz;
        internal const int NumLagsStage1 = MaxpitchStage1 - MinpitchStage1 + 1;
        internal const int NumlagsCoarse = CoarseFsKhz * (MaxpitchMs - MinpitchMs);
        private const int NumlagsFs = FsKhz * (MaxpitchMs - MinpitchMs);
        private const int Numstates1 = 24;
        private const bool LowComplexity = false;
        private const bool LowRate = false;

        internal const int CeCap = (2 * FsKhz / Stage1FsKhz) * NumLagsStage1 * SmplPitch.NumSubframes + 64;

        private static readonly float[] DownsampFilt =
        {
            -0.045472838f, 0.0f, 0.06366198f, 0.0f, -0.10610329f, 0.0f, 0.31830987f,
            0.5f, 0.31830987f, 0.0f, -0.10610329f, 0.0f, 0.06366198f, 0.0f, -0.045472838f,
        };

        private static readonly float[] InterpolFiltC =
        {
            -0.0024414062f, 0.023925781f, -0.119628906f, 0.59814453f,
            0.59814453f, -0.119628906f, 0.023925783f, -0.0024414062f,
        };

        /// <summary>smpl_filt_arma1 with b={1,-1}, a={1,-0.96}, zero state (MA1 then the C's 5-way unrolled AR1).</summary>
        private static void PitchHpFilter(float[] x, int n, float[] o, int oOff)
        {
            float stateMa = 0;
            for (int i = 0; i < n; i++)
            {
                o[oOff + i] = x[i] - stateMa;
                stateMa = x[i];
            }

            const float ar1 = 0.96f;
            float ar12 = ar1 * ar1;
            float ar13 = ar1 * ar12;
            float ar14 = ar1 * ar13;
            float ar15 = ar1 * ar14;
            float ytmp = 0;
            int idx = 0;
            while (idx + 4 < n)
            {
                int b = oOff + idx;
                float x0 = o[b], x1 = o[b + 1], x2 = o[b + 2], x3 = o[b + 3], x4 = o[b + 4];
                o[b + 4] = x4 + ar1 * x3 + ar12 * x2 + ar13 * x1 + ar14 * x0 + ar15 * ytmp;
                o[b] = x0 + ar1 * ytmp;
                o[b + 1] = x1 + ar1 * x0 + ar12 * ytmp;
                o[b + 2] = x2 + ar1 * x1 + ar12 * x0 + ar13 * ytmp;
                o[b + 3] = x3 + ar1 * x2 + ar12 * x1 + ar13 * x0 + ar14 * ytmp;
                ytmp = o[b + 4];
                idx += 5;
            }

            while (idx < n)
            {
                ytmp = o[oOff + idx] + ytmp * ar1;
                o[oOff + idx] = ytmp;
                idx++;
            }
        }

        private static int PitchDownsample(float[] ptrIn, int l, float[] ptrOut)
        {
            const int d = DownsampDelay;
            int n = (l - 2 * d) / 2;
            for (int j = 0; j < n; j++)
            {
                float tmp = ptrIn[2 * j + d] * DownsampFilt[d];
                for (int i = 0; i < d; i += 2)
                {
                    tmp += (ptrIn[2 * j + i] + ptrIn[2 * j + 2 * d - i]) * DownsampFilt[i];
                }

                ptrOut[j] = tmp;
            }

            return n;
        }

        private static void UpsampECore(float[] buf, int xEnd, int yEnd, int length)
        {
            int xi = xEnd;
            int yi = yEnd;
            for (int k = 0; k < length; k++)
            {
                float v = (buf[xi] + buf[xi + 1]) * 0.5f;
                buf[yi] = v;
                yi--;
                buf[yi] = buf[xi];
                yi--;
                xi--;
            }
        }

        private static void UpsampCCore(float[] buf, int xEnd, int yEnd, int length)
        {
            int xi = xEnd;
            int yi = yEnd;
            for (int k = 0; k < length; k++)
            {
                float tmp = 0;
                for (int j = 0; j < InterpolDelayC; j++)
                {
                    float a = buf[xi + j - (InterpolDelayC - 1)];
                    float b = buf[xi + InterpolDelayC - j];
                    tmp += (a + b) * InterpolFiltC[j];
                }

                buf[yi] = tmp;
                yi--;
                buf[yi] = buf[xi];
                yi--;
                xi--;
            }
        }

        private static float Nrg(float[] x, int off, int n)
        {
            float s = 0;
            for (int i = 0; i < n; i++)
            {
                float v = x[off + i];
                s += v * v;
            }

            return s;
        }

        private static float Maximum(float[] x, int off, int n)
        {
            float m = x[off];
            for (int i = 1; i < n; i++)
            {
                if (x[off + i] > m)
                {
                    m = x[off + i];
                }
            }

            return m;
        }

        private static int GetMaxi(float[] x, int n)
        {
            int bi = 0;
            float best = x[0];
            for (int i = 1; i < n; i++)
            {
                if (x[i] > best)
                {
                    best = x[i];
                    bi = i;
                }
            }

            return bi;
        }

        private static int[] GetMaxiK(float[] x, int n, int k, bool[] taken)
        {
            Array.Clear(taken, 0, n);
            var o = new List<int>(k);
            for (int c = 0; c < k; c++)
            {
                int bi = -1;
                float best = 0;
                for (int i = 0; i < n; i++)
                {
                    if (!taken[i] && (bi < 0 || x[i] > best))
                    {
                        best = x[i];
                        bi = i;
                    }
                }

                if (bi < 0)
                {
                    break;
                }

                taken[bi] = true;
                o.Add(bi);
            }

            return o.ToArray();
        }

        private static float DotProd(float[] a, int aOff, float[] b, int bOff, int n)
        {
            float r = 0;
            for (int i = 0; i < n; i++)
            {
                r += a[aOff + i] * b[bOff + i];
            }

            return r;
        }

        private static void CalcE1Inner(float[] e1, float[] ltpbuf, int t, int minpitch, int maxpitch, int lagSubfrlen)
        {
            int numlags = maxpitch - minpitch + 1;
            int reg0 = t - minpitch;
            e1[0] = MlowMath.MaxF32(Nrg(ltpbuf, reg0, lagSubfrlen), 1e-9f);
            for (int i = 1; i < numlags; i++)
            {
                float rm = ltpbuf[reg0 - i];
                float rs = ltpbuf[reg0 + lagSubfrlen - i];
                e1[i] = MlowMath.MaxF32(e1[i - 1] + rm * rm - rs * rs, 1e-9f);
            }
        }

        private static void CalcE1(float[] e1, float[] e1Ext, float[] ltpbuf, int ltpbufLen, int numsubfrs, int minpitch, int maxpitch, int lagSubfrlen)
        {
            int numlags = maxpitch - minpitch + 1;
            int maxpitchExt = maxpitch + (numsubfrs - 1) * lagSubfrlen;
            int numlagsExt = maxpitchExt - minpitch + 1;
            int t = ltpbufLen - lagSubfrlen;
            CalcE1Inner(e1Ext, ltpbuf, t, minpitch, maxpitchExt, lagSubfrlen);
            int offset = numlagsExt - numlags;
            for (int sf = 0; sf < numsubfrs; sf++)
            {
                Array.Copy(e1Ext, offset, e1, sf * numlags, numlags);
                offset -= lagSubfrlen;
            }
        }

        private static void CalcCE2(float[] c, float[] e2, float[] ltpbuf, int ltpbufLen, int numsubfrs)
        {
            int t = ltpbufLen - LagSubfrlenStage1 * numsubfrs;
            for (int sf = 0; sf < numsubfrs; sf++)
            {
                int reg0 = t - MinpitchStage1;
                for (int i = 0; i < NumLagsStage1; i++)
                {
                    c[sf * NumLagsStage1 + i] = DotProd(ltpbuf, t, ltpbuf, reg0 - i, 20);
                }

                e2[sf] = MlowMath.MaxF32(DotProd(ltpbuf, t, ltpbuf, t, 20), 1e-9f);
                t += LagSubfrlenStage1;
            }
        }

        private static void UpsampEFast(float[] buf, int numsubfrs, ref int minpitch, ref int numlags)
        {
            int nin = numlags;
            int nout = (nin - 1) * 2;
            for (int sf = numsubfrs - 1; sf >= 0; sf--)
            {
                UpsampECore(buf, sf * nin + nin - 2, sf * nout + nout - 1, nin - 1);
            }

            numlags = nout;
            minpitch *= 2;
        }

        private static void UpsampCFast(float[] buf, int numsubfrs, ref int minpitch, ref int numlags)
        {
            int nin = numlags;
            int nout = (nin - InterpolDelayC) * 2;
            for (int sf = numsubfrs - 1; sf >= 0; sf--)
            {
                UpsampCCore(buf, sf * nin + nin - 1 - InterpolDelayC, sf * nout + nout - 1, nin - (InterpolDelayC * 2 - 1));
            }

            numlags = nout;
            minpitch *= 2;
        }

        private static int Sumdeltas(int[] laginds, int numsubfrs)
        {
            int ret = 0;
            for (int i = 1; i < numsubfrs; i++)
            {
                int d = laginds[i] - laginds[i - 1];
                ret += d < 0 ? -d : d;
            }

            return ret;
        }

        /// <summary>ec_encode_wrap with a NULL context: -log2((fh-fl)/ft).</summary>
        private static float EcEncodeBits(uint fl, uint fh, uint ft)
        {
            float p = ((float)fh - (float)fl) / (float)ft;
            if (p <= 0.0f)
            {
                return 0.0f;
            }

            return -(float)MlowMath.Log2(p);
        }

        /// <summary>smpl_encode_lags with a NULL range coder: the bit cost used as a survivor bias.</summary>
        private static float EncodeLagsBits(PitchTables tab, int blocksegsIx, int[] laginds, int prevLagblk, int prevLagidx, int mode)
        {
            unchecked
            {
                float nBits = 0;
                int ixJulia = tab.Blocksegs2Idx[blocksegsIx];
                const int blocksize = PitchblockMs * FsKhz * 2;
                PitchBlockSeg pblockseg = tab.Blocksegs[blocksegsIx];

                if (prevLagblk < 0)
                {
                    uint[] cmf = tab.BlocksegIdxCmf;
                    nBits += EcEncodeBits(cmf[ixJulia - 1], cmf[ixJulia], cmf[tab.Blocksegs.Length]);
                }
                else
                {
                    uint[] cmf = tab.BlockTransitionCmf[prevLagblk];
                    int b0 = pblockseg.Blocks[0];
                    nBits += EcEncodeBits(cmf[b0], cmf[b0 + 1], cmf[SmplPitch.PitchNumBlocks]);
                    int startIx = tab.FirstblockRange[b0][0];
                    int cmfLen = tab.FirstblockRange[b0][1] - tab.FirstblockRange[b0][0] + 1;
                    uint[] cmf2 = tab.BlocksegIdxCmf;
                    uint c0 = cmf2[startIx];
                    int lo = ixJulia - startIx - 1;
                    int hi = ixJulia - startIx;
                    nBits += EcEncodeBits(cmf2[startIx + lo] - c0, cmf2[startIx + hi] - c0, cmf2[startIx + cmfLen] - c0);
                }

                int blk = pblockseg.Blocks[0];
                int deltaBlk = blk - prevLagblk;
                int startSeg = 0;
                int lagindsIx = 0;
                if (!(prevLagblk > -1 && deltaBlk >= -1 && deltaBlk <= 2))
                {
                    nBits += 6.0f;
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
                    nBits += EcEncodeBits(deltaLagCmf[cmfBase + ix] - p0, deltaLagCmf[cmfBase + ix + 1] - p0, deltaLagCmf[cmfBase + blocksize] - p0);
                    prevLagblk = blk;
                    prevLagidx = idx;
                }

                return nBits;
            }
        }

        private static float SpectralHarmCached(float avgLag, float[] f2w, float[] cache, bool reset)
        {
            if (reset)
            {
                for (int i = 0; i < cache.Length; i++)
                {
                    cache[i] = SmplSignalMode.HarmonicityUndef;
                }
            }

            float invF2StepHz = 2.0f * (float)(SmplLpc.FLen - 1) / 16000.0f;
            float harmHz = 16000.0f / avgLag;
            int harmIx = (int)MlowMath.GoRound(harmHz * 2.0f * invF2StepHz);
            if (harmIx < 0 || harmIx >= cache.Length)
            {
                return SmplSignalMode.HarmStrengthAt(avgLag, f2w);
            }

            if (cache[harmIx] > SmplSignalMode.HarmonicityUndef)
            {
                return cache[harmIx];
            }

            float hs = SmplSignalMode.HarmStrengthAt(avgLag, f2w);
            cache[harmIx] = hs;
            return hs;
        }

        private static float GetPrevLagBias(PitchEstState st, float lag)
        {
            float lagDiff = Math.Abs(lag - st.PrevLag);
            float diffThres = PrevwghtSpan * st.PrevLag;
            if (lagDiff < diffThres)
            {
                return st.PrevPitchCorr * (1.0f - lagDiff / diffThres) * Prevwght;
            }

            return 0.0f;
        }

        public static PitchResult Estimate(PitchEstState st, float[] ltpBuf, float[] f2, bool codedAsActiveVoice)
        {
            return Estimate(st, ltpBuf, f2, codedAsActiveVoice, new PitchEncScratch());
        }

        /// <summary>
        /// Full pitch estimator. ltpBuf is the perceptually-weighted speech (MaxLtpBufLen; the last
        /// LookaheadLen samples are lookahead); f2 the LPC power spectrum. Mutates the predictor in st.
        /// </summary>
        public static PitchResult Estimate(PitchEstState st, float[] ltpBuf, float[] f2, bool codedAsActiveVoice, PitchEncScratch s)
        {
            PitchTables tab = SmplPitch.LoadPitchTables();
            const int numsubfrs = SmplPitch.NumSubframes;
            const int l = SmplPitch.MaxLtpBufLen;
            const int look = LookaheadLen;
            const int numBlocks = SmplPitch.PitchNumBlocks;
            var res = new PitchResult();

            if (!codedAsActiveVoice)
            {
                float minLag = (float)(MinpitchMs * FsKhz);
                st.PrevLag = 0.0f;
                st.PrevPitchCorr = 0.0f;
                st.PrevLagblk = -1;
                st.PrevLagidx = -1;
                res.AvgLag = minLag;
                for (int i = 0; i < numsubfrs; i++)
                {
                    res.Lags[i] = minLag;
                }

                return res;
            }

            const int offset = DownsampDelay;
            float[] stage1 = s.Stage1;
            Array.Clear(stage1, 0, stage1.Length);
            PitchHpFilter(ltpBuf, l, stage1, offset);
            const int hpLen = l - look;
            float[] ltpBufHp = s.LtpBufHp;
            Array.Copy(stage1, offset, ltpBufHp, 0, hpLen);

            float[] stage1Ds = s.Stage1Ds;
            Array.Clear(stage1Ds, 0, stage1Ds.Length);
            int stage1Len = PitchDownsample(stage1, l + offset, stage1Ds);

            const int numlags0 = NumLagsStage1;
            float[] e1 = s.E1;
            Array.Clear(e1, 0, e1.Length);
            CalcE1(e1, s.E1Ext, stage1Ds, stage1Len, numsubfrs, MinpitchStage1, MaxpitchStage1, LagSubfrlenStage1);
            float[] e2 = s.E2;
            Array.Clear(e2, 0, e2.Length);
            float[] c = s.C;
            float[] e = s.E;
            Array.Clear(c, 0, c.Length);
            Array.Clear(e, 0, e.Length);
            CalcCE2(c, e2, stage1Ds, stage1Len, numsubfrs);

            int numlags = numlags0;
            float[] sqrtE1 = s.SqrtE1;
            for (int sf = 0; sf < numsubfrs; sf++)
            {
                for (int i = 0; i < numlags; i++)
                {
                    sqrtE1[i] = (float)Math.Sqrt(e1[sf * numlags + i] + 1e-30f);
                }

                float sqrtE2 = (float)Math.Sqrt(e2[sf] + 1e-30f);
                for (int i = 0; i < numlags; i++)
                {
                    float tmp = 0.5f * (sqrtE1[i] + sqrtE2);
                    e[sf * numlags + i] = tmp * tmp;
                }
            }

            int minpitchC = MinpitchStage1;
            int numlagsC = numlags;
            int minpitchE = MinpitchStage1;
            int numlagsE = numlags;
            if (LowComplexity)
            {
                UpsampEFast(c, numsubfrs, ref minpitchC, ref numlagsC);
            }
            else
            {
                UpsampCFast(c, numsubfrs, ref minpitchC, ref numlagsC);
            }

            UpsampEFast(e, numsubfrs, ref minpitchE, ref numlagsE);

            const int minpitchCoarse = CoarseFsKhz * MinpitchMs;
            const int numlagsCoarse = NumlagsCoarse;
            int offsetC0 = minpitchCoarse - minpitchC;
            int offsetE0 = minpitchCoarse - minpitchE;

            float[] h = s.H;
            Array.Clear(h, 0, h.Length);
            for (int sf = 0; sf < numsubfrs; sf++)
            {
                for (int i = 0; i < numlagsCoarse; i++)
                {
                    h[sf * numlagsCoarse + i] = c[sf * numlagsC + offsetC0 + i] / e[sf * numlagsE + offsetE0 + i];
                }
            }

            const int pitchblockCoarse = PitchblockMs * CoarseFsKhz;
            var hblk = new float[numsubfrs * numBlocks];
            for (int sf = 0; sf < numsubfrs; sf++)
            {
                for (int block = 0; block < numBlocks; block++)
                {
                    hblk[sf * numBlocks + block] = Maximum(h, sf * numlagsCoarse + block * pitchblockCoarse, pitchblockCoarse);
                }
            }

            int blocksizeFs = Pitchblock * 2;
            const float reductionFactor = 0.7f;
            float pitchDeltawght = PitchDeltawght / (float)blocksizeFs;
            var sfWght = new float[numsubfrs];
            float sumE2 = 0;
            for (int sf = 0; sf < numsubfrs; sf++)
            {
                sumE2 += e2[sf];
            }

            for (int sf = 0; sf < numsubfrs; sf++)
            {
                sfWght[sf] = e2[sf] / sumE2;
            }

            int numBlocktracks = tab.Blocktracks.Length;
            float[] utils = s.Utils;
            for (int i = 0; i < numBlocktracks; i++)
            {
                PitchBlockTrack bt = tab.Blocktracks[i];
                float corr = 0;
                for (int sf = 0; sf < numsubfrs; sf++)
                {
                    corr += hblk[sf * numBlocks + bt.Track[sf]] * sfWght[sf];
                }

                float shortlagbias1 = ((float)MaxpitchLen / ((bt.Meanblock + 1.5f) * (float)Pitchblock) - 1.0f) * PitchShortwght1;
                utils[i] = 1.0f / (1.1f - corr) - reductionFactor * (float)Pitchblock * pitchDeltawght * bt.Trackdeltas + shortlagbias1;
            }

            int[] trackIdx = GetMaxiK(utils, numBlocktracks, Numstates1, s.Taken);

            float[] e1Fs = s.E1Fs;
            Array.Clear(e1Fs, 0, e1Fs.Length);
            CalcE1(e1Fs, s.E1Ext, ltpBufHp, l - look, numsubfrs, minpitchE, minpitchE + numlagsE - 1, LagSubfrlen);

            var uniqueblocks = new int[numsubfrs];
            foreach (int ti in trackIdx)
            {
                int[] track = tab.Blocktracks[ti].Track;
                for (int sf = 0; sf < numsubfrs; sf++)
                {
                    uniqueblocks[sf] |= 1 << track[sf];
                }
            }

            float hThres = LowComplexity ? 0.0f : 0.25f;
            int offsetC = MinpitchMs * FsKhz - minpitchC;
            int offsetE = MinpitchMs * FsKhz - minpitchE;
            float[] sqrtE1Blk = s.SqrtE1Blk;
            for (int sf = 0; sf < numsubfrs; sf++)
            {
                int mask = 1;
                int cPtr = offsetC + sf * numlagsC;
                int ePtr = offsetE + sf * numlagsE;
                int e1Ptr = offsetE + sf * numlagsE;
                int hPtr = sf * NumlagsFs;
                int ltpOff = (l - look) + (sf - numsubfrs) * LagSubfrlen;
                float e2sf = MlowMath.MaxF32(DotProd(ltpBufHp, ltpOff, ltpBufHp, ltpOff, 40), 1e-9f);
                e2[sf] = e2sf;
                float sqrtE2 = (float)Math.Sqrt(e2sf + 1e-30f);
                for (int block = 0; block < numBlocks; block++)
                {
                    if ((uniqueblocks[sf] & mask) != 0)
                    {
                        for (int i = 0; i < Pitchblock + 1; i++)
                        {
                            sqrtE1Blk[i] = (float)Math.Sqrt(e1Fs[e1Ptr + block * Pitchblock + i] + 1e-30f);
                        }

                        for (int i = 0; i < Pitchblock + 1; i++)
                        {
                            float tmp = 0.5f * (sqrtE1Blk[i] + sqrtE2);
                            e[ePtr + block * Pitchblock + i] = 0.5f * tmp * tmp;
                        }

                        for (int i = 0; i < Pitchblock; i++)
                        {
                            if (h[hPtr + block * Pitchblock + i] > hThres)
                            {
                                int lag = MinpitchLen + block * Pitchblock + i;
                                c[cPtr + block * Pitchblock + i] = 0.5f * DotProd(ltpBufHp, ltpOff, ltpBufHp, ltpOff - lag, 40);
                            }
                        }
                    }

                    mask <<= 1;
                }
            }

            int strideC = numBlocks * 2 * Pitchblock + offsetC;
            int strideE = numBlocks * 2 * Pitchblock + offsetE;
            for (int sf = numsubfrs - 1; sf >= 0; sf--)
            {
                int cPtr = offsetC + sf * numlagsC;
                int cPtrFrac = offsetC + sf * strideC;
                int ePtr = offsetE + sf * numlagsE;
                int ePtrFrac = offsetE + sf * strideE;
                int hPtr = sf * 2 * Pitchblock * numBlocks;
                int mask = 1 << (numBlocks - 1);
                for (int block = numBlocks - 1; block >= 0; block--)
                {
                    if ((uniqueblocks[sf] & mask) != 0)
                    {
                        int ein = ePtr + block * Pitchblock;
                        int eout = ePtrFrac + block * 2 * Pitchblock;
                        UpsampECore(e, ein + Pitchblock - 1, eout + 2 * Pitchblock - 1, Pitchblock);
                        int cin = cPtr + block * Pitchblock;
                        int cout = cPtrFrac + block * 2 * Pitchblock;
                        if (LowComplexity)
                        {
                            UpsampECore(c, cin + Pitchblock - 1, cout + 2 * Pitchblock - 1, Pitchblock);
                        }
                        else
                        {
                            UpsampCCore(c, cin + Pitchblock - 1, cout + 2 * Pitchblock - 1, Pitchblock);
                        }

                        for (int i = 0; i < 2 * Pitchblock; i++)
                        {
                            h[hPtr + block * 2 * Pitchblock + i] = c[cout + i] / e[eout + i];
                        }
                    }

                    mask >>= 1;
                }
            }

            List<int[]> lagindsSurv = s.LagindsSurv;
            List<int> blocksegsIxList = s.BlocksegsIxList;
            lagindsSurv.Clear();
            blocksegsIxList.Clear();
            float[] hComb = s.HComb;
            Dictionary<int, int> lagindCache = s.LagindCache;
            lagindCache.Clear();
            foreach (int idx in trackIdx)
            {
                int[] rng = tab.BlocksegsIx[idx];
                for (int j = 0; j < rng[1]; j++)
                {
                    int bsx = rng[0] + j;
                    PitchBlockSeg pblockseg = tab.Blocksegs[bsx];
                    int[] row = RentRow(s, lagindsSurv.Count);
                    int startSf = 0;
                    for (int n = 0; n < pblockseg.Nblocks; n++)
                    {
                        int lookupKey = (((startSf << 3) + pblockseg.Seglens[n]) << 4) | pblockseg.Blocks[n];
                        int bestI;
                        if (!lagindCache.TryGetValue(lookupKey, out bestI))
                        {
                            Array.Clear(hComb, 0, hComb.Length);
                            for (int sf = startSf; sf < startSf + pblockseg.Seglens[n]; sf++)
                            {
                                int hPtr = sf * 2 * Pitchblock * numBlocks + pblockseg.Blocks[n] * 2 * Pitchblock;
                                for (int i = 0; i < 2 * Pitchblock; i++)
                                {
                                    hComb[i] += h[hPtr + i] * e2[sf];
                                }
                            }

                            bestI = GetMaxi(hComb, hComb.Length);
                            lagindCache[lookupKey] = bestI;
                        }

                        for (int sf = startSf; sf < startSf + pblockseg.Seglens[n]; sf++)
                        {
                            row[sf] = bestI + pblockseg.Blocks[n] * 2 * Pitchblock;
                        }

                        startSf += pblockseg.Seglens[n];
                    }

                    lagindsSurv.Add(row);
                    blocksegsIxList.Add(bsx);
                }
            }

            int nlaginds = lagindsSurv.Count;
            float pitchRatewght = LowRate ? 0.028f : RatewghtHr;
            float[] f2w = SmplSignalMode.BuildF2w(f2);
            int maxIx = GetMaxi(sfWght, numsubfrs);
            float[] spectralHarmCache = s.HarmCache;
            Array.Clear(spectralHarmCache, 0, spectralHarmCache.Length);

            float bestUtil = 0, bestPitchcorr = 0;
            int bestSurv = 0;
            float pitchDeltawghtFs = PitchDeltawght / (float)blocksizeFs;
            for (int surv = 0; surv < nlaginds; surv++)
            {
                int[] li = lagindsSurv[surv];
                float sumC = 0, sumE = 0;
                for (int sf = 0; sf < numsubfrs; sf++)
                {
                    sumC += c[offsetC + sf * strideC + li[sf]];
                    sumE += e[offsetE + sf * strideE + li[sf]];
                }

                float rateBias = EncodeLagsBits(tab, blocksegsIxList[surv], li, st.PrevLagblk, st.PrevLagidx, 1) * pitchRatewght;
                float meanLag = (float)li[maxIx] * 0.5f + (float)MinpitchLen;
                float pitchcorr = sumC / sumE;
                float firstLag = 0.5f * (float)li[0] + (float)MinpitchLen;
                float prevLagBias = GetPrevLagBias(st, firstLag);
                float spectralHarmBias = SpecHarmBias * SpectralHarmCached(meanLag, f2w, spectralHarmCache, surv == 0);
                float util = 1.0f / (1.1f - pitchcorr) - pitchDeltawghtFs * (float)Sumdeltas(li, numsubfrs) + spectralHarmBias + prevLagBias - rateBias;
                if (surv == 0 || util > bestUtil)
                {
                    bestUtil = util;
                    bestSurv = surv;
                }

                if (surv == 0 || pitchcorr > bestPitchcorr)
                {
                    bestPitchcorr = pitchcorr;
                }
            }

            int[] best = lagindsSurv[bestSurv];
            for (int sf = 0; sf < numsubfrs; sf++)
            {
                res.Lags[sf] = (float)best[sf] * 0.5f + (float)MinpitchLen;
                res.Laginds[sf] = best[sf];
            }

            res.AvgLag = (float)best[maxIx] * 0.5f + (float)MinpitchLen;
            res.HarmStrength = SpectralHarmCached(res.AvgLag, f2w, spectralHarmCache, false);
            res.Pitchcorr = bestPitchcorr;
            res.BlocksegIdx = blocksegsIxList[bestSurv];

            st.PrevLag = res.Lags[numsubfrs - 1];
            st.PrevPitchCorr = bestPitchcorr;
            st.PrevLagidx = best[numsubfrs - 1];
            st.PrevLagblk = st.PrevLagidx / (2 * Pitchblock);
            return res;
        }

        private static int[] RentRow(PitchEncScratch s, int i)
        {
            while (s.RowPool.Count <= i)
            {
                s.RowPool.Add(new int[SmplPitch.NumSubframes]);
            }

            int[] row = s.RowPool[i];
            Array.Clear(row, 0, row.Length);
            return row;
        }
    }
}
