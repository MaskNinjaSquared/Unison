// =============================================================================
// CelpEnc
//
// Encoder-side CELP excitation, per subframe: perceptually-weighted impulse
// response, ACB/LTP search (voiced), FCB pulse search (greedy or
// delayed-decision beam), gain quantization, and the reconstructed LPC
// excitation that feeds the persistent ACB / zero-input-response state.
// Ports: meowcaller mlow/celp_enc.go (MIT)
// =============================================================================
using System;

namespace Unison.Voip.Engine.Mlow
{
    /// <summary>Per-subframe CELP encode result (index 0 = FEC, 1 = main).</summary>
    internal sealed class CelpSubframeOut
    {
        public readonly short[][] Pulses = new short[SmplPerc.SmplCelpMaxRates][];
        public readonly short[] NPulses = new short[SmplPerc.SmplCelpMaxRates];
        public readonly short[] AcbIdx = new short[SmplPerc.SmplCelpMaxRates];
        public readonly short[] GainIdx = new short[SmplPerc.SmplCelpMaxRates];
        public float[] ExcLpc;
    }

    internal sealed class CelpEncoder
    {
        private const int MaxSfLen = SmplPerc.SmplMaxSfLen;
        private const int MaxLResp = SmplPerc.SmplMaxLResp;
        private const int Order = MlowMath.SmplLpcOrder;
        private const int AcbgM = CelpTables.AcbgM;
        private const int AcbgN = CelpTables.AcbgN;
        private const int FcbgVN = CelpTables.FcbgVN;
        private const int FcbgVDeltaN = CelpTables.FcbgVDeltaN;
        private const int UvGainIdxLen = CelpTables.UvGainIdxLen;
        private const int Fec = SmplPerc.SmplCelpIdxFec;
        private const int Main = SmplPerc.SmplCelpIdxMain;
        private const int MaxRates = SmplPerc.SmplCelpMaxRates;
        private const int MaxPulsesPerSf = SmplPerc.SmplMaxPulsesPerSf;

        private const int LtpInterpolDelay = 8;
        private const int LagSubfrlen = 40;
        private const int MaxpitchLen = 320;

        private const float GAcbRdMu = 0.014999999664723873f;
        private const float VGainMinDb = -100.0f;
        private const float VGainMaxDb = 0.0f;
        private const float VGainStepDb = 3.0f;
        private const float UvGainMinDb = -90.0f;
        private const float UvGainMaxDb = 0.0f;
        private const float UvGainStepDb = 1.0f;

        private const float RateAcbScale = 0.9f;
        private const float PitchSharpeningCoef = 0.9881f;
        private const int FcbSrvMax = 4;
        private const int MaxNumsurv = 8;
        private const int NGainSteps = 2;

        private static readonly Lazy<Tables> TablesInst = new Lazy<Tables>(BuildTables);

        private readonly float[] _stateWghtBuf = new float[MaxSfLen + Order];
        private readonly float[] _stateErrLpcSyn = new float[Order];
        private readonly float[] _hanningWin;
        private readonly ulong[] _sgntrs = new ulong[MaxSfLen];
        private readonly int _acbStateLen;
        private readonly int[] _prevAcbIdx = { -1, -1 };
        private readonly int[] _prevFcbIdx = { -1, -1 };
        private int _subfrCnt;
        private readonly int _subfrPerPacket;
        private readonly int _fcbSubfrlen;
        private readonly int _percRespLen;
        private readonly bool _lowRate;
        private readonly bool _ignoreZir = false;
        private readonly bool _useMa9;

        private readonly float[] _impLpcBuf = new float[MaxSfLen + Order];
        private readonly float[] _phi = new float[MaxSfLen];
        private readonly float[] _phiFlip = new float[2 * MaxSfLen];

        private readonly float[] _impLpcRev = new float[2 * MaxLResp - 1];
        private readonly float[] _imp;
        private readonly float[] _resLpcPad;
        private readonly float[] _dLpc = new float[MaxSfLen];
        private readonly float[] _zirLpc = new float[MaxSfLen];
        private readonly float[] _zirTmp = new float[MaxSfLen + MaxLResp - 1];
        private readonly float[] _htZir = new float[2 * MaxLResp - 1];
        private readonly float[] _acbBasis = new float[MaxSfLen * AcbgM];
        private readonly float[] _acb = new float[MaxSfLen];
        private readonly float[] _dLtp = new float[MaxSfLen];
        private readonly float[] _wtgtTmp = new float[MaxSfLen + 2 * MaxLResp - 1];
        private readonly float[] _wtgt = new float[MaxSfLen + MaxLResp];
        private readonly short[][] _pulses = { new short[MaxPulsesPerSf], new short[MaxPulsesPerSf] };
        private readonly float[] _excFcb = new float[MaxSfLen];
        private readonly float[] _excFcbRaw = new float[MaxSfLen];
        private readonly float[] _lpcResErr = new float[MaxSfLen];
        private readonly float[] _tmp = new float[MaxSfLen];
        private readonly float[] _dAbs = new float[MaxSfLen];
        private readonly float[] _dSign = new float[MaxSfLen];
        private readonly float[] _dNew = new float[MaxSfLen];
        private readonly float[] _num = new float[MaxSfLen];
        private readonly float[] _den = new float[MaxSfLen];
        private readonly float[] _q = new float[MaxSfLen];
        private readonly float[] _qAdd = new float[MaxSfLen];
        private readonly float[] _ddDen = new float[MaxSfLen];
        private readonly bool[] _taken = new bool[MaxSfLen];
        private readonly AcbgParams _acbg;
        private readonly FcbSearchScratch _sc = new FcbSearchScratch();
        private readonly FcbState[] _bestFcbState = { new FcbState(), new FcbState() };

        public CelpEncoder(bool lowRate, int percRespLen, int fcbSubfrlen, int subfrPerPacket)
        {
            _acbStateLen = fcbSubfrlen + MaxpitchLen + LtpInterpolDelay;

            ulong s = 0x9E3779B97F4A7C15UL;
            for (int i = 0; i < _sgntrs.Length; i++)
            {
                s = unchecked(s * 6364136223846793005UL + 1442695040888963407UL);
                _sgntrs[i] = s;
            }

            _hanningWin = new float[percRespLen];
            float scale = 1.0f / (float)(2 * SmplPerc.SmplPercRespLen + 1);
            for (int i = 0; i < percRespLen; i++)
            {
                _hanningWin[i] = (float)Math.Sin(MlowMath.SmplPI * (float)(percRespLen + i + 1) * scale);
            }

            AcbState = new float[CelpDec.MaxPitchLag + MaxSfLen + LtpInterpolDelay];
            _subfrPerPacket = subfrPerPacket;
            _fcbSubfrlen = fcbSubfrlen;
            _percRespLen = percRespLen;
            _lowRate = lowRate;
            _useMa9 = percRespLen == 10;
            _imp = new float[percRespLen];
            _resLpcPad = new float[fcbSubfrlen + percRespLen + 1];
            _acbg = new AcbgParams(AcbgM * fcbSubfrlen);
        }

        /// <summary>Persistent adaptive-codebook excitation history.</summary>
        internal float[] AcbState { get; }

        private sealed class Tables
        {
            public readonly float[] AcbgInvProbLR = new float[(AcbgN + 1) * AcbgN];
            public readonly float[] AcbgInvProbHR = new float[(AcbgN + 1) * AcbgN];
            public readonly float[] FcbgainsV = new float[FcbgVN];
            public readonly float[] FcbgainsUV = new float[UvGainIdxLen + 1];
            public readonly float[] FcbgVInvProb = new float[FcbgVN];
            public readonly float[] FcbgVDeltaInvProb = new float[FcbgVDeltaN];
        }

        private sealed class AcbgParams
        {
            public float WerrIn;
            public readonly float[] PhiAcb = new float[AcbgM * AcbgM];
            public readonly float[] DAcbLpc = new float[AcbgM];
            public readonly float[] AcbBasisPhi;

            public AcbgParams(int n)
            {
                AcbBasisPhi = new float[n];
            }
        }

        private struct Fcb
        {
            public float Wnrg;
            public int NPulses;
            public int PosNew;
            public float SignNew;
            public ulong Sgntr;
            public int FcbStateIdx;
        }

        private sealed class FcbState
        {
            public readonly int[] PulsePositions = new int[MaxSfLen];
            public readonly float[] PulseSigns = new float[MaxSfLen];
            public readonly float[] Num = new float[MaxSfLen];
            public readonly float[] Den = new float[MaxSfLen];

            public void CloneFrom(FcbState o)
            {
                Array.Copy(o.PulsePositions, PulsePositions, MaxSfLen);
                Array.Copy(o.PulseSigns, PulseSigns, MaxSfLen);
                Array.Copy(o.Num, Num, MaxSfLen);
                Array.Copy(o.Den, Den, MaxSfLen);
            }
        }

        private sealed class FcbSearchScratch
        {
            public readonly FcbState[][] FcbStates = { NewStates(), NewStates() };
            public int ReadIdx;
            public int WriteIdx = 1;
            public readonly Fcb[] Fcbs = new Fcb[MaxNumsurv];
            public int FcbsSize;
            public readonly Fcb[] FcbCandidates = new Fcb[MaxNumsurv * MaxNumsurv];
            public int FcbCandidatesSize;
            public readonly ulong[] UniqueSgntr = new ulong[MaxNumsurv * MaxNumsurv];
            public int UniqueSgntrSize;

            private static FcbState[] NewStates()
            {
                var v = new FcbState[MaxNumsurv];
                for (int i = 0; i < v.Length; i++)
                {
                    v[i] = new FcbState();
                }

                return v;
            }

            public void SwapRw()
            {
                int t = ReadIdx;
                ReadIdx = WriteIdx;
                WriteIdx = t;
            }

            public bool IsUnique(ulong sgntr)
            {
                for (int i = 0; i < UniqueSgntrSize; i++)
                {
                    if (UniqueSgntr[i] == sgntr)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        // --- leaf helpers -----------------------------------------------------

        private static float DotProd(float[] a, int aOff, float[] b, int bOff, int l)
        {
            float r = 0;
            for (int i = 0; i < l; i++)
            {
                r += a[aOff + i] * b[bOff + i];
            }

            return r;
        }

        private static float Nrg(float[] x, int n)
        {
            float s = 0;
            for (int k = 0; k < n; k++)
            {
                s += x[k] * x[k];
            }

            return s;
        }

        private static void Reverse(float[] x, int off, int l)
        {
            for (int i = 0; i < l / 2; i++)
            {
                float t = x[off + i];
                x[off + i] = x[off + l - i - 1];
                x[off + l - i - 1] = t;
            }
        }

        private static void Q(float[] num, float[] den, int l, float[] q)
        {
            for (int i = 0; i < l; i++)
            {
                q[i] = (num[i] * num[i]) / den[i];
            }
        }

        /// <summary>Symmetric Toeplitz multiply; c (from cOff) carries the trailing zero at 2*lResp-1, x is zero padded to n+lResp.</summary>
        private static void MultSymtoepl2(float[] c, int cOff, int lResp, float[] x, int xOff, float[] y, int n)
        {
            int length = lResp;
            int nn = 0;
            while (nn < lResp - 1)
            {
                y[nn] = DotProd(c, cOff + lResp - 1 - nn, x, xOff, length);
                length++;
                nn++;
            }

            length = 2 * lResp;
            while (nn < n - lResp)
            {
                y[nn] = DotProd(c, cOff, x, xOff + nn + 1 - lResp, length);
                nn++;
            }

            while (nn < n)
            {
                length--;
                y[nn] = DotProd(c, cOff, x, xOff + nn + 1 - lResp, length);
                nn++;
            }
        }

        /// <summary>16th-order AR filter; the 16-sample state sits in y[yBase-16 .. yBase).</summary>
        private static void FiltAr16(float[] x, int n, float[] coef, int yBase, float[] y)
        {
            for (int nn = 0; nn < n; nn++)
            {
                float res = x[nn];
                for (int i = 0; i < 16; i++)
                {
                    res -= coef[16 - i] * y[yBase + nn - 16 + i];
                }

                y[yBase + nn] = res;
            }
        }

        /// <summary>MA filter; (coefLen-1) history samples sit before x[xBase].</summary>
        private static void FiltMa(float[] x, int xBase, int n, float[] coef, int coefLen, float[] y, int yOff)
        {
            int i;
            if (coef[0] == 1.0f)
            {
                for (int k = 0; k < n; k++)
                {
                    y[yOff + k] = x[xBase + k] + coef[1] * x[xBase + k - 1];
                }

                i = 2;
            }
            else
            {
                for (int k = 0; k < n; k++)
                {
                    y[yOff + k] = coef[0] * x[xBase + k];
                }

                i = 1;
            }

            for (; i < coefLen; i++)
            {
                for (int k = 0; k < n; k++)
                {
                    y[yOff + k] += coef[i] * x[xBase + k - i];
                }
            }
        }

        private static void FiltMa9(float[] x, int xBase, int n, float[] coef, float[] y, int yOff)
        {
            for (int nn = 0; nn < n; nn++)
            {
                float res = 0;
                for (int i = 0; i < 10; i++)
                {
                    res += coef[i] * x[xBase + nn - i];
                }

                y[yOff + nn] = res;
            }
        }

        private void PercFiltMa(float[] x, int xBase, int n, float[] coef, int coefLen, float[] y, int yOff)
        {
            if (_useMa9)
            {
                FiltMa9(x, xBase, n, coef, y, yOff);
            }
            else
            {
                FiltMa(x, xBase, n, coef, coefLen, y, yOff);
            }
        }

        private static void CmfToBits(ushort[] cmf, int cmfLen, float[] bits, int bitsOff)
        {
            for (int i = 0; i < cmfLen - 1; i++)
            {
                bits[bitsOff + i] = -(float)MlowMath.Log2((float)unchecked((ushort)(cmf[i + 1] - cmf[i])) / (float)cmf[cmfLen - 1]);
            }
        }

        private static int GetMaxi(float[] x, int xLen)
        {
            int i = 0;
            float mx = x[0];
            for (int n = 1; n < xLen; n++)
            {
                if (x[n] > mx)
                {
                    mx = x[n];
                    i = n;
                }
            }

            return i;
        }

        private void GetMaxiK(float[] x, int[] idx, int xLen, int k)
        {
            bool[] taken = _taken;
            Array.Clear(taken, 0, xLen);
            for (int kk = 0; kk < k; kk++)
            {
                float best = -float.MaxValue;
                int bi = 0;
                bool found = false;
                for (int n = 0; n < xLen; n++)
                {
                    if (!taken[n] && (!found || x[n] > best))
                    {
                        best = x[n];
                        bi = n;
                        found = true;
                    }
                }

                taken[bi] = true;
                idx[kk] = bi;
            }
        }

        private static Tables BuildTables()
        {
            var t = new Tables();
            var cmf = new ushort[AcbgN + 1];
            for (int i = 0; i < AcbgN + 1; i++)
            {
                CelpTables.CelpDcmfToCmf(CelpTables.CelpAcbgainsDcmfLR, i * AcbgN, AcbgN, cmf);
                CmfToBits(cmf, AcbgN + 1, t.AcbgInvProbLR, i * AcbgN);
                CelpTables.CelpDcmfToCmf(CelpTables.CelpAcbgainsDcmfHR, i * AcbgN, AcbgN, cmf);
                CmfToBits(cmf, AcbgN + 1, t.AcbgInvProbHR, i * AcbgN);
                for (int j = 0; j < AcbgN; j++)
                {
                    t.AcbgInvProbLR[i * AcbgN + j] = (float)Math.Pow(2.0, t.AcbgInvProbLR[i * AcbgN + j] * GAcbRdMu);
                    t.AcbgInvProbHR[i * AcbgN + j] = (float)Math.Pow(2.0, t.AcbgInvProbHR[i * AcbgN + j] * GAcbRdMu);
                }
            }

            var fcbgVCmf = new ushort[FcbgVN + 1];
            var fcbgVDeltaCmf = new ushort[FcbgVDeltaN + 1];
            CelpTables.CelpDcmfToCmf(CelpTables.CelpFcbgVDcmf, 0, FcbgVN, fcbgVCmf);
            CelpTables.CelpDcmfToCmf(CelpTables.CelpFcbgVDeltaDcmf, 0, FcbgVDeltaN, fcbgVDeltaCmf);
            CmfToBits(fcbgVCmf, FcbgVN + 1, t.FcbgVInvProb, 0);
            for (int i = 0; i < FcbgVN; i++)
            {
                t.FcbgVInvProb[i] = (float)Math.Pow(2.0, t.FcbgVInvProb[i] * GAcbRdMu);
            }

            CmfToBits(fcbgVDeltaCmf, FcbgVDeltaN + 1, t.FcbgVDeltaInvProb, 0);
            for (int i = 0; i < FcbgVDeltaN; i++)
            {
                t.FcbgVDeltaInvProb[i] = (float)Math.Pow(2.0, t.FcbgVDeltaInvProb[i] * GAcbRdMu);
            }

            for (int ix = 0; ix < FcbgVN; ix++)
            {
                float db = (float)ix * VGainStepDb + VGainMinDb;
                t.FcbgainsV[ix] = (float)Math.Pow(10.0, 0.05f * db);
            }

            for (int ix = 0; ix <= UvGainIdxLen; ix++)
            {
                float db = (float)ix * UvGainStepDb + UvGainMinDb;
                t.FcbgainsUV[ix] = (float)Math.Pow(10.0, 0.05f * db);
            }

            return t;
        }

        // --- LTP / ACB synthesis ------------------------------------------------

        private static void AcbDequant(bool lowRate, int acbIdx, float[] acbG)
        {
            short[] cb = lowRate ? CelpTables.CbAcbgainsLRQ14 : CelpTables.CbAcbgainsHRQ14;
            const float scQ14 = 1.0f / (float)(1 << 14);
            for (int m = 0; m < AcbgM; m++)
            {
                acbG[m] = (float)cb[acbIdx * AcbgM + m] * scQ14;
            }
        }

        private static void AcbSynthesize(int fcbSubfrlen, float[] acbBasis, float[] acbG, float[] acb)
        {
            for (int i = 0; i < fcbSubfrlen; i++)
            {
                acb[i] = acbBasis[i] * acbG[0];
            }

            for (int i = 0; i < fcbSubfrlen; i++)
            {
                acb[i] += acbG[1] * acbBasis[fcbSubfrlen + i];
            }
        }

        private static void PitchSharp(float[] x, int lag, int l)
        {
            for (int i = lag; i < l; i++)
            {
                x[i] += x[i - lag] * PitchSharpeningCoef;
            }
        }

        /// <summary>Builds the LTP basis per 40-sample sub-block, extending state in place.</summary>
        private static void SynLtpBasis(float[] lags, int nLags, float[] state, int stateLen, float[] acbBasis)
        {
            float[] kernel = CelpDec.InterpolKernel;
            int p = stateLen - nLags * LagSubfrlen;
            for (int subfr = 0; subfr < nLags; subfr++)
            {
                int il = (int)Math.Floor(lags[subfr]);
                if ((float)il == lags[subfr])
                {
                    for (int i = 0; i < LagSubfrlen; i++)
                    {
                        state[p + i] = state[(p + i) - il];
                    }

                    for (int i = 0; i < LagSubfrlen; i++)
                    {
                        acbBasis[subfr * LagSubfrlen + i] = state[p + i];
                    }

                    for (int i = 0; i < LagSubfrlen; i++)
                    {
                        float a = state[(p + i) - il - 1];
                        float b = state[(p + i) - il + 1];
                        acbBasis[(nLags + subfr) * LagSubfrlen + i] = a + b;
                    }
                }
                else
                {
                    float first = DotProd(state, p + (-1 - il - LtpInterpolDelay), kernel, 0, 2 * LtpInterpolDelay);
                    int srcBase = p + (-il - LtpInterpolDelay);
                    for (int nn = 0; nn < LagSubfrlen; nn++)
                    {
                        float ret = 0;
                        for (int i = 0; i < 8; i++)
                        {
                            float s0 = state[srcBase + nn + i];
                            float s1 = state[srcBase + nn + 15 - i];
                            ret += (s0 + s1) * kernel[i];
                        }

                        state[p + nn] = ret;
                    }

                    float last = DotProd(state, p + (LagSubfrlen - 1 - il - LtpInterpolDelay), kernel, 0, 2 * LtpInterpolDelay);
                    for (int i = 0; i < LagSubfrlen; i++)
                    {
                        acbBasis[subfr * LagSubfrlen + i] = state[p + i];
                    }

                    int b1 = (nLags + subfr) * LagSubfrlen;
                    acbBasis[b1] = first + state[p + 1];
                    for (int i = 0; i < LagSubfrlen - 2; i++)
                    {
                        acbBasis[b1 + 1 + i] = state[p + i] + state[p + i + 2];
                    }

                    const int iLast = LagSubfrlen - 1;
                    acbBasis[b1 + iLast] = state[p + iLast - 1] + last;
                }

                p += LagSubfrlen;
            }
        }

        // --- FCB search helpers -------------------------------------------------

        private static void CalcDAbsAndSign(float[] d, int l, float[] dAbs, float[] dSign)
        {
            for (int i = 0; i < l; i++)
            {
                if (d[i] > 0.0f)
                {
                    dAbs[i] = d[i];
                    dSign[i] = 1.0f;
                }
                else
                {
                    dAbs[i] = -d[i];
                    dSign[i] = -1.0f;
                }
            }
        }

        private static bool CheckIfBetter(float wnrg, ref float nrgThr, float wnrgPerPulse)
        {
            nrgThr += wnrgPerPulse;
            if (wnrg > nrgThr)
            {
                nrgThr = wnrg;
                return true;
            }

            return false;
        }

        private static int PhiColOffset(int col)
        {
            return MaxSfLen - col;
        }

        private static void NonZeroRange(int col, int percRespLen, int fcbSubfrlen, out int lo, out int hi)
        {
            lo = col - percRespLen + 1;
            if (lo < 0)
            {
                lo = 0;
            }

            hi = col + percRespLen;
            if (hi > fcbSubfrlen)
            {
                hi = fcbSubfrlen;
            }
        }

        // --- greedy FCB search (smpl_fcb_search) --------------------------------

        private void FcbSearch(float[] d, float[] wnrgPerPulse, short[] fcbPulsesMax, short[][] pulses, short[] nPulses, float[] wnrg, float[] gainFromSearch, float[] fcbWnrg)
        {
            int fcbSubfrlen = _fcbSubfrlen;
            int percRespLen = _percRespLen;
            nPulses[0] = 0;
            nPulses[1] = 0;

            var positions = new int[MaxPulsesPerSf];
            float[] dAbs = _dAbs;
            float[] dSign = _dSign;
            float[] num = _num;
            float[] den = _den;
            float[] q = _q;
            Array.Clear(dAbs, 0, MaxSfLen);
            Array.Clear(dSign, 0, MaxSfLen);
            Array.Clear(num, 0, MaxSfLen);
            Array.Clear(den, 0, MaxSfLen);
            Array.Clear(q, 0, MaxSfLen);
            float phi0 = _phi[0];
            CalcDAbsAndSign(d, fcbSubfrlen, dAbs, dSign);

            for (int i = 0; i < fcbSubfrlen; i++)
            {
                den[i] = phi0 + 1e-16f;
            }

            Array.Copy(dAbs, num, fcbSubfrlen);
            positions[0] = GetMaxi(num, fcbSubfrlen);
            var nrgThr = new float[MaxRates];
            int p0 = positions[0];
            float ratio = num[p0] / den[p0];
            float wnrg0 = num[p0] * ratio;
            if (CheckIfBetter(wnrg0, ref nrgThr[Main], wnrgPerPulse[Main]))
            {
                nPulses[Main] = 1;
                wnrg[Main] = wnrg0;
                wnrg[Fec] = wnrg0;
                gainFromSearch[Main] = ratio;
                gainFromSearch[Fec] = ratio;
                fcbWnrg[Main] = den[p0];
                fcbWnrg[Fec] = den[p0];
                if (fcbPulsesMax[Fec] > 0)
                {
                    nPulses[Fec] = nPulses[Main];
                    wnrg[Fec] = wnrg[Main];
                    gainFromSearch[Fec] = gainFromSearch[Main];
                    fcbWnrg[Fec] = fcbWnrg[Main];
                }
            }

            for (int pulseNr = 1; pulseNr < fcbPulsesMax[Main]; pulseNr++)
            {
                int position = positions[pulseNr - 1];
                float sgn = dSign[position];
                for (int i = 0; i < fcbSubfrlen; i++)
                {
                    num[i] += dAbs[position];
                }

                int nz0, nz1;
                NonZeroRange(position, percRespLen, fcbSubfrlen, out nz0, out nz1);
                int colOff = PhiColOffset(position);
                float dDen = 0;
                for (int i = 0; i < pulseNr - 1; i++)
                {
                    int pi = positions[i];
                    dDen += _phiFlip[colOff + pi] * dSign[pi];
                }

                dDen *= 2.0f * sgn;
                dDen += _phiFlip[colOff + position];
                for (int i = 0; i < fcbSubfrlen; i++)
                {
                    den[i] += dDen;
                }

                for (int i = nz0; i < nz1; i++)
                {
                    den[i] += 2.0f * sgn * dSign[i] * _phiFlip[colOff + i];
                }

                Q(num, den, fcbSubfrlen, q);
                positions[pulseNr] = GetMaxi(q, fcbSubfrlen);
                int pp = positions[pulseNr];
                if (CheckIfBetter(q[pp], ref nrgThr[Main], wnrgPerPulse[Main]))
                {
                    nPulses[Main] = (short)(pulseNr + 1);
                    wnrg[Main] = q[pp];
                    gainFromSearch[Main] = num[pp] / den[pp];
                    fcbWnrg[Main] = den[pp];
                }

                if (fcbPulsesMax[Fec] >= pulseNr && CheckIfBetter(q[pp], ref nrgThr[Fec], wnrgPerPulse[Fec]))
                {
                    nPulses[Fec] = (short)(pulseNr + 1);
                    wnrg[Fec] = q[pp];
                    gainFromSearch[Fec] = num[pp] / den[pp];
                    fcbWnrg[Fec] = den[pp];
                }
            }

            for (int r = Fec; r <= Main; r++)
            {
                if (nrgThr[r] > 0.0f)
                {
                    for (int i = 0; i < nPulses[r]; i++)
                    {
                        int position = positions[i];
                        pulses[r][i] = dSign[position] > 0.0f ? (short)(1 + position) : (short)-(1 + position);
                    }
                }
                else
                {
                    wnrg[r] = 0.0f;
                    gainFromSearch[r] = 0.0f;
                    fcbWnrg[r] = 0.0f;
                    nPulses[r] = 0;
                }
            }
        }

        // --- delayed-decision beam FCB search -----------------------------------

        private void AddPulse(FcbSearchScratch sc, int fcbIdxIn, float[] dAbs, float[] dSign, int numsurv, int idx, int lag, float pitchSharp)
        {
            int fcbSubfrlen = _fcbSubfrlen;
            int percRespLen = _percRespLen;

            int fcbPosNew = sc.Fcbs[fcbIdxIn].PosNew;
            float fcbSignNew = sc.Fcbs[fcbIdxIn].SignNew;
            int fcbNPulses = sc.Fcbs[fcbIdxIn].NPulses;
            int fcbStateIdx = sc.Fcbs[fcbIdxIn].FcbStateIdx;
            ulong fcbSgntrBase = sc.Fcbs[fcbIdxIn].Sgntr;

            FcbState rs = sc.FcbStates[sc.ReadIdx][fcbStateIdx];
            FcbState ws = sc.FcbStates[sc.WriteIdx][idx];

            float add = dAbs[fcbPosNew];
            for (int i = 0; i < fcbSubfrlen; i++)
            {
                ws.Num[i] = rs.Num[i] + add;
            }

            for (int i = 0; i < fcbSubfrlen; i++)
            {
                ws.Den[i] = rs.Den[i];
            }

            if (pitchSharp == 0.0f)
            {
                int nz0, nz1;
                NonZeroRange(fcbPosNew, percRespLen, fcbSubfrlen, out nz0, out nz1);
                int colOff = PhiColOffset(fcbPosNew);
                float dDen = 0;
                for (int i = 0; i < fcbNPulses; i++)
                {
                    dDen += _phiFlip[colOff + rs.PulsePositions[i]] * rs.PulseSigns[i];
                }

                dDen *= 2.0f * fcbSignNew;
                dDen += _phiFlip[colOff + fcbPosNew];
                for (int i = 0; i < fcbSubfrlen; i++)
                {
                    ws.Den[i] += dDen;
                }

                for (int i = nz0; i < nz1; i++)
                {
                    ws.Den[i] += 2.0f * fcbSignNew * dSign[i] * _phiFlip[colOff + i];
                }
            }
            else
            {
                float dDen = 0;
                float g1 = 1.0f;
                for (int pos = fcbPosNew; pos < fcbSubfrlen; pos += lag)
                {
                    int colOff = PhiColOffset(pos);
                    for (int i = 0; i < fcbNPulses; i++)
                    {
                        float g2 = g1;
                        int pulsePos = rs.PulsePositions[i];
                        float pulseSgn = rs.PulseSigns[i];
                        for (int posq = pulsePos; posq < fcbSubfrlen; posq += lag)
                        {
                            dDen += g2 * _phiFlip[colOff + posq] * pulseSgn;
                            g2 *= pitchSharp;
                        }
                    }

                    g1 *= pitchSharp;
                }

                dDen *= 2.0f * fcbSignNew;
                g1 = 1.0f;
                for (int pos1 = fcbPosNew; pos1 < fcbSubfrlen; pos1 += lag)
                {
                    int colOff = PhiColOffset(pos1);
                    float g2 = g1;
                    for (int pos2 = fcbPosNew; pos2 < fcbSubfrlen; pos2 += lag)
                    {
                        dDen += g2 * _phiFlip[colOff + pos2];
                        g2 *= pitchSharp;
                    }

                    g1 *= pitchSharp;
                }

                for (int i = 0; i < fcbSubfrlen; i++)
                {
                    ws.Den[i] += dDen;
                }

                float[] ddDen = _ddDen;
                Array.Clear(ddDen, 0, ddDen.Length);
                g1 = 1.0f;
                for (int pos = fcbPosNew; pos < fcbSubfrlen; pos += lag)
                {
                    int nz0, nz1;
                    NonZeroRange(pos, percRespLen, fcbSubfrlen, out nz0, out nz1);
                    int colOff = PhiColOffset(pos);
                    float g2 = g1;
                    for (int k = 0; k < fcbSubfrlen; k += lag)
                    {
                        int startI = nz0 - k;
                        if (startI < 0)
                        {
                            startI = 0;
                        }

                        int endI = fcbSubfrlen - k;
                        if (nz1 - k < endI)
                        {
                            endI = nz1 - k;
                        }

                        for (int i = startI; i < endI; i++)
                        {
                            ddDen[i] += g2 * _phiFlip[colOff + i + k];
                        }

                        g2 *= pitchSharp;
                    }

                    g1 *= pitchSharp;
                }

                for (int i = 0; i < fcbSubfrlen; i++)
                {
                    ws.Den[i] += 2.0f * fcbSignNew * dSign[i] * ddDen[i];
                }
            }

            for (int i = 0; i < fcbNPulses; i++)
            {
                ws.PulsePositions[i] = rs.PulsePositions[i];
                ws.PulseSigns[i] = rs.PulseSigns[i];
            }

            ws.PulsePositions[fcbNPulses] = fcbPosNew;
            ws.PulseSigns[fcbNPulses] = fcbSignNew;

            int newNPulses = fcbNPulses + 1;
            float[] q = _qAdd;
            Q(ws.Num, ws.Den, fcbSubfrlen, q);
            var sortIx = new int[MaxNumsurv];
            GetMaxiK(q, sortIx, fcbSubfrlen, numsurv);
            for (int i = 0; i < numsurv; i++)
            {
                int pos = sortIx[i];
                ulong sgntr = unchecked(fcbSgntrBase + _sgntrs[pos]);
                if (sc.IsUnique(sgntr))
                {
                    sc.FcbCandidates[sc.FcbCandidatesSize] = new Fcb { Wnrg = q[pos], NPulses = newNPulses, PosNew = pos, SignNew = dSign[pos], Sgntr = sgntr, FcbStateIdx = idx };
                    sc.FcbCandidatesSize++;
                    sc.UniqueSgntr[sc.UniqueSgntrSize] = sgntr;
                    sc.UniqueSgntrSize++;
                }
            }
        }

        private void FcbSearchDeldec(float[] d, float pitchSharp, int lag, float[] wnrgPerPulse, short[] fcbPulsesMax, short[] surv, short[][] pulses, short[] nPulses, float[] wnrg, float[] gainFromSearch, float[] fcbWnrg)
        {
            int fcbSubfrlen = _fcbSubfrlen;
            FcbSearchScratch sc = _sc;
            sc.FcbsSize = 0;
            sc.FcbCandidatesSize = 0;
            sc.UniqueSgntrSize = 0;

            float[] dNew = _dNew;
            float[] dAbs = _dAbs;
            float[] dSign = _dSign;
            Array.Clear(dNew, 0, MaxSfLen);
            Array.Clear(dAbs, 0, MaxSfLen);
            Array.Clear(dSign, 0, MaxSfLen);
            float phi0 = _phi[0];

            if (pitchSharp != 0.0f && lag > 0 && lag < fcbSubfrlen)
            {
                Array.Copy(d, dNew, fcbSubfrlen);
                for (int j = 0; j < fcbSubfrlen; j++)
                {
                    float g = pitchSharp;
                    for (int i = lag + j; i < fcbSubfrlen; i += lag)
                    {
                        dNew[j] += g * d[i];
                        g *= pitchSharp;
                    }
                }

                CalcDAbsAndSign(dNew, fcbSubfrlen, dAbs, dSign);
            }
            else
            {
                CalcDAbsAndSign(d, fcbSubfrlen, dAbs, dSign);
                pitchSharp = 0.0f;
            }

            sc.ReadIdx = 0;
            sc.WriteIdx = 1;
            var bestFcb = new Fcb[MaxRates];
            FcbState[] bestFcbState = _bestFcbState;
            var nrgThr = new float[MaxRates];

            {
                FcbState w0 = sc.FcbStates[sc.WriteIdx][0];
                Array.Copy(dAbs, w0.Num, fcbSubfrlen);
                if (pitchSharp == 0.0f)
                {
                    for (int i = 0; i < fcbSubfrlen; i++)
                    {
                        w0.Den[i] = phi0 + 1e-16f;
                    }
                }
                else
                {
                    int offset = fcbSubfrlen - 1;
                    for (int i = fcbSubfrlen - 1; i >= 0; i -= lag)
                    {
                        float res = 1e-16f;
                        float g1 = 1.0f;
                        for (int j = i; j < fcbSubfrlen; j += lag)
                        {
                            int colOff = PhiColOffset(j);
                            float g2 = 1.0f;
                            for (int k = i; k < fcbSubfrlen; k += lag)
                            {
                                res += g1 * g2 * _phiFlip[colOff + k];
                                g2 *= pitchSharp;
                            }

                            g1 *= pitchSharp;
                        }

                        int length = lag;
                        if (offset + 1 < length)
                        {
                            length = offset + 1;
                        }

                        for (int jj = 0; jj < length; jj++)
                        {
                            w0.Den[offset - jj] = res;
                        }

                        offset -= length;
                    }
                }
            }

            sc.SwapRw();
            float[] q = _q;
            Array.Clear(q, 0, MaxSfLen);
            {
                FcbState r0 = sc.FcbStates[sc.ReadIdx][0];
                if (pitchSharp == 0.0f)
                {
                    Array.Copy(r0.Num, q, fcbSubfrlen);
                }
                else
                {
                    Q(r0.Num, r0.Den, fcbSubfrlen, q);
                }
            }

            var sortIx = new int[MaxNumsurv];
            GetMaxiK(q, sortIx, fcbSubfrlen, surv[0]);
            sc.FcbsSize = 0;
            {
                FcbState r0 = sc.FcbStates[sc.ReadIdx][0];
                for (int i = 0; i < surv[0]; i++)
                {
                    int pos = sortIx[i];
                    sc.Fcbs[sc.FcbsSize] = new Fcb
                    {
                        Sgntr = _sgntrs[pos],
                        PosNew = pos,
                        SignNew = dSign[pos],
                        Wnrg = (r0.Num[pos] * r0.Num[pos]) / r0.Den[pos],
                    };
                    sc.FcbsSize++;
                }
            }

            CheckIfBetterDeldec(sc, false, 0, ref bestFcb[Main], bestFcbState[Main], ref nrgThr[Main], wnrgPerPulse[Main]);
            if (fcbPulsesMax[Fec] > 0)
            {
                CheckIfBetterDeldec(sc, false, 0, ref bestFcb[Fec], bestFcbState[Fec], ref nrgThr[Fec], wnrgPerPulse[Fec]);
            }

            if (fcbPulsesMax[Main] > 1)
            {
                for (int pulseNr = 2; pulseNr < fcbPulsesMax[Main]; pulseNr++)
                {
                    sc.FcbCandidatesSize = 0;
                    sc.UniqueSgntrSize = 0;
                    int fcbsSize = sc.FcbsSize;
                    for (int i = 0; i < fcbsSize; i++)
                    {
                        AddPulse(sc, i, dAbs, dSign, surv[pulseNr - 1], i, lag, pitchSharp);
                    }

                    sc.SwapRw();
                    int candSize = sc.FcbCandidatesSize;
                    for (int i = 0; i < candSize; i++)
                    {
                        q[i] = sc.FcbCandidates[i].Wnrg;
                    }

                    GetMaxiK(q, sortIx, candSize, surv[pulseNr - 1]);
                    sc.FcbsSize = 0;
                    for (int i = 0; i < surv[pulseNr - 1]; i++)
                    {
                        sc.Fcbs[sc.FcbsSize] = sc.FcbCandidates[sortIx[i]];
                        sc.FcbsSize++;
                    }

                    CheckIfBetterDeldec(sc, false, 0, ref bestFcb[Main], bestFcbState[Main], ref nrgThr[Main], wnrgPerPulse[Main]);
                    if (fcbPulsesMax[Fec] >= pulseNr)
                    {
                        CheckIfBetterDeldec(sc, false, 0, ref bestFcb[Fec], bestFcbState[Fec], ref nrgThr[Fec], wnrgPerPulse[Fec]);
                    }
                }

                sc.FcbCandidatesSize = 0;
                sc.UniqueSgntrSize = 0;
                int fcbsSizeLast = sc.FcbsSize;
                for (int i = 0; i < fcbsSizeLast; i++)
                {
                    AddPulse(sc, i, dAbs, dSign, 1, i, lag, pitchSharp);
                }

                sc.SwapRw();
                int bestIdx = 0;
                float maxWnrg = sc.FcbCandidates[0].Wnrg;
                for (int i = 1; i < sc.FcbCandidatesSize; i++)
                {
                    if (sc.FcbCandidates[i].Wnrg > maxWnrg)
                    {
                        maxWnrg = sc.FcbCandidates[i].Wnrg;
                        bestIdx = i;
                    }
                }

                CheckIfBetterDeldec(sc, true, bestIdx, ref bestFcb[Main], bestFcbState[Main], ref nrgThr[Main], wnrgPerPulse[Main]);
            }

            for (int r = Fec; r <= Main; r++)
            {
                FcbState bs = bestFcbState[r];
                for (int i = 0; i < bestFcb[r].NPulses; i++)
                {
                    pulses[r][i] = bs.PulseSigns[i] > 0.0f ? (short)(1 + bs.PulsePositions[i]) : (short)-(1 + bs.PulsePositions[i]);
                }

                pulses[r][bestFcb[r].NPulses] = bestFcb[r].SignNew > 0.0f ? (short)(1 + bestFcb[r].PosNew) : (short)-(1 + bestFcb[r].PosNew);
                if (bestFcb[r].Wnrg > 0.0f)
                {
                    wnrg[r] = bestFcb[r].Wnrg;
                    int pn = bestFcb[r].PosNew;
                    gainFromSearch[r] = bs.Num[pn] / bs.Den[pn];
                    fcbWnrg[r] = bs.Den[pn];
                    nPulses[r] = (short)(bestFcb[r].NPulses + 1);
                }
                else
                {
                    wnrg[r] = 0.0f;
                    gainFromSearch[r] = 0.0f;
                    fcbWnrg[r] = 0.0f;
                    nPulses[r] = 0;
                }
            }
        }

        private static void CheckIfBetterDeldec(FcbSearchScratch sc, bool fromCand, int idx, ref Fcb bestFcb, FcbState bestFcbState, ref float nrgThr, float wnrgPerPulse)
        {
            nrgThr += wnrgPerPulse;
            Fcb f = fromCand ? sc.FcbCandidates[idx] : sc.Fcbs[idx];
            if (f.Wnrg > nrgThr)
            {
                nrgThr = f.Wnrg;
                bestFcb = f;
                bestFcbState.CloneFrom(sc.FcbStates[sc.ReadIdx][f.FcbStateIdx]);
            }
        }

        // --- gain quant -----------------------------------------------------------

        private static float Wnrg2(float[] c, float[] x)
        {
            return x[0] * (c[0] * x[0] + c[1] * x[1]) + x[1] * (c[2] * x[0] + c[3] * x[1]);
        }

        private static float Wnrg3(float[] c, float[] x)
        {
            return x[0] * (c[0] * x[0] + c[1] * x[1] + c[2] * x[2])
                + x[1] * (c[3] * x[0] + c[4] * x[1] + c[5] * x[2])
                + x[2] * (c[6] * x[0] + c[7] * x[1] + c[8] * x[2]);
        }

        private static short QuantGainUv(float gainFromSearch)
        {
            float gainDb = 20.0f * (float)Math.Log10(gainFromSearch + 1.0e-16f);
            if (gainDb < UvGainMinDb)
            {
                gainDb = UvGainMinDb;
            }

            if (gainDb > UvGainMaxDb)
            {
                gainDb = UvGainMaxDb;
            }

            return (short)MlowMath.GoRound((gainDb - UvGainMinDb) / UvGainStepDb);
        }

        private static void FcbSynthesize(int fcbSubfrlen, short[] pulses, int nPulses, float[] fcb)
        {
            for (int i = 0; i < fcbSubfrlen; i++)
            {
                fcb[i] = 0.0f;
            }

            for (int n = 0; n < nPulses; n++)
            {
                int sign = 1 + 2 * (pulses[n] >> 15);
                int pos = pulses[n] * sign - 1;
                fcb[pos] += (float)sign;
            }
        }

        private float[] AcbgInvProb()
        {
            return _lowRate ? TablesInst.Value.AcbgInvProbLR : TablesInst.Value.AcbgInvProbHR;
        }

        private int CalcAcbGain(int lResp, float[] acbBasis, float[] dLpc, AcbgParams acbg, float[] dLtp)
        {
            int fcbSubfrlen = _fcbSubfrlen;
            for (int m = 0; m < AcbgM; m++)
            {
                int cOff = MaxSfLen - lResp + 1;
                float[] tmp = _tmp;
                Array.Clear(tmp, 0, tmp.Length);
                MultSymtoepl2(_phiFlip, cOff, lResp, acbBasis, m * fcbSubfrlen, tmp, fcbSubfrlen);
                Array.Copy(tmp, 0, acbg.AcbBasisPhi, m * fcbSubfrlen, fcbSubfrlen);
                for (int i = 0; i < AcbgM; i++)
                {
                    acbg.PhiAcb[m * AcbgM + i] = DotProd(acbBasis, i * fcbSubfrlen, acbg.AcbBasisPhi, m * fcbSubfrlen, fcbSubfrlen);
                }

                acbg.DAcbLpc[m] = DotProd(acbBasis, m * fcbSubfrlen, dLpc, 0, fcbSubfrlen);
            }

            float bestRd = 1e30f;
            int bestAcbgIdx = 0;
            int transitionIdx = _prevAcbIdx[Main] != -1 ? _prevAcbIdx[Main] + 1 : 0;
            float[] invProb = AcbgInvProb();
            int invOff = transitionIdx * AcbgN;
            short[] cb = _lowRate ? CelpTables.CbAcbgainsLRQ14 : CelpTables.CbAcbgainsHRQ14;
            const float scQ14 = 1.0f / (float)(1 << 14);
            var acbGains = new float[AcbgM];
            for (int n = 0; n < AcbgN; n++)
            {
                for (int m = 0; m < AcbgM; m++)
                {
                    acbGains[m] = (float)cb[n * AcbgM + m] * scQ14;
                }

                float werrOut = acbg.WerrIn + Wnrg2(acbg.PhiAcb, acbGains)
                    - 2.0f * (acbg.DAcbLpc[0] * acbGains[0] + acbg.DAcbLpc[1] * acbGains[1]);
                float rd = werrOut * invProb[invOff + n];
                if (rd < bestRd)
                {
                    bestRd = rd;
                    bestAcbgIdx = n;
                }
            }

            float g0 = -(float)cb[bestAcbgIdx * AcbgM] * scQ14;
            for (int i = 0; i < fcbSubfrlen; i++)
            {
                dLtp[i] = dLpc[i] + g0 * acbg.AcbBasisPhi[i];
            }

            float g1 = -(float)cb[bestAcbgIdx * AcbgM + 1] * scQ14;
            for (int i = 0; i < fcbSubfrlen; i++)
            {
                dLtp[i] += g1 * acbg.AcbBasisPhi[fcbSubfrlen + i];
            }

            return bestAcbgIdx;
        }

        private float CalcGainsV(float fcbWnrg, float gainFromSearch, float[] excFcb, float[] dLpc, AcbgParams acbg, int rateIdx, short[] acbIdx, short[] fcbIdx)
        {
            Tables tbl = TablesInst.Value;
            int fcbSubfrlen = _fcbSubfrlen;

            float fcbgain = gainFromSearch;
            if (fcbgain < 0.0f)
            {
                fcbgain = 0.0f;
            }

            float gainDb = 20.0f * (float)Math.Log10(fcbgain + 1.0e-16f);
            if (gainDb < VGainMinDb)
            {
                gainDb = VGainMinDb;
            }

            if (gainDb > VGainMaxDb)
            {
                gainDb = VGainMaxDb;
            }

            int maxGainIdx = (int)MlowMath.GoRound((VGainMaxDb - VGainMinDb) / VGainStepDb);

            int bestAcbgIdx = 0;
            int bestFcbgIdx = 0;

            var acbFcb = new float[AcbgM];
            for (int i = 0; i < AcbgM; i++)
            {
                acbFcb[i] = DotProd(acbg.AcbBasisPhi, i * fcbSubfrlen, excFcb, 0, fcbSubfrlen);
            }

            const int stride = AcbgM + 1;
            var phiAll = new float[stride * stride];
            for (int i = 0; i < AcbgM; i++)
            {
                for (int j = 0; j < AcbgM; j++)
                {
                    phiAll[i * stride + j] = acbg.PhiAcb[i * AcbgM + j];
                }
            }

            for (int i = 0; i < AcbgM; i++)
            {
                phiAll[i * stride + AcbgM] = acbFcb[i];
                phiAll[AcbgM * stride + i] = acbFcb[i];
            }

            phiAll[AcbgM * stride + AcbgM] = fcbWnrg;

            var dall = new float[AcbgM + 1];
            dall[0] = acbg.DAcbLpc[0];
            dall[1] = acbg.DAcbLpc[1];
            dall[AcbgM] = DotProd(dLpc, 0, excFcb, 0, fcbSubfrlen);

            var gainIdxs = new int[NGainSteps];
            var fcbgains = new float[NGainSteps];
            var fcbgInvProb = new float[NGainSteps];
            int firstGainIdx = (int)Math.Floor((gainDb - VGainMinDb) / VGainStepDb) - (NGainSteps - 1) / 2;
            if (firstGainIdx < 0)
            {
                firstGainIdx = 0;
            }

            if (firstGainIdx > maxGainIdx - 1)
            {
                firstGainIdx = maxGainIdx - 1;
            }

            int offset = (int)Math.Floor((VGainMinDb - VGainMaxDb) / VGainStepDb);
            for (int i = 0; i < NGainSteps; i++)
            {
                gainIdxs[i] = firstGainIdx + i;
                fcbgains[i] = tbl.FcbgainsV[gainIdxs[i]];
                if (_prevFcbIdx[rateIdx] == -1)
                {
                    fcbgInvProb[i] = tbl.FcbgVInvProb[gainIdxs[i]];
                }
                else
                {
                    int delta = _prevFcbIdx[rateIdx] - gainIdxs[i];
                    fcbgInvProb[i] = tbl.FcbgVDeltaInvProb[delta - offset];
                }
            }

            float bestRd = 1e30f;
            int transitionIdx = _prevAcbIdx[rateIdx] != -1 ? _prevAcbIdx[rateIdx] + 1 : 0;
            short[] cb = _lowRate ? CelpTables.CbAcbgainsLRQ14 : CelpTables.CbAcbgainsHRQ14;
            float[] invProb = AcbgInvProb();
            int invOff = transitionIdx * AcbgN;
            const float scQ14 = 1.0f / (float)(1 << 14);
            var gains = new float[AcbgM + 1];
            for (int n = 0; n < AcbgN; n++)
            {
                for (int m = 0; m < AcbgM; m++)
                {
                    gains[m] = (float)cb[n * AcbgM + m] * scQ14;
                }

                for (int i = 0; i < NGainSteps; i++)
                {
                    gains[AcbgM] = fcbgains[i];
                    float werrOut = acbg.WerrIn + Wnrg3(phiAll, gains)
                        - 2.0f * (dall[0] * gains[0] + dall[1] * gains[1] + dall[2] * gains[2]);
                    float rd = werrOut * fcbgInvProb[i] * invProb[invOff + n];
                    if (rd < bestRd)
                    {
                        bestRd = rd;
                        bestAcbgIdx = n;
                        bestFcbgIdx = gainIdxs[i];
                    }
                }
            }

            acbIdx[rateIdx] = (short)bestAcbgIdx;
            fcbIdx[rateIdx] = (short)bestFcbgIdx;
            if (fcbIdx[rateIdx] < 0)
            {
                fcbIdx[rateIdx] = 0;
            }

            if (fcbIdx[rateIdx] > maxGainIdx)
            {
                fcbIdx[rateIdx] = (short)maxGainIdx;
            }

            return tbl.FcbgainsV[fcbIdx[rateIdx]];
        }

        /// <summary>
        /// smpl_celp_encoder for one subframe. predcoef is the monic LPC [17]; lags holds 3 entries
        /// (lags[1] &gt; 0 means voiced); subfrImportance / fcbPulsesMax are per rate.
        /// </summary>
        public CelpSubframeOut EncodeSubframe(float[] resLpc, float[] predcoef, float[] percWghtResp, float[] lags, float[] subfrImportance, short[] fcbPulsesMax, short[] surv)
        {
            int lResp = _percRespLen;
            int fcbSubfrlen = _fcbSubfrlen;
            bool voiced = lags[1] > 0.0f;

            FiltAr16(percWghtResp, lResp, predcoef, Order, _impLpcBuf);
            for (int i = 0; i < lResp; i++)
            {
                _impLpcBuf[Order + i] *= _hanningWin[i];
            }

            float[] impLpcRev = _impLpcRev;
            Array.Clear(impLpcRev, 0, impLpcRev.Length);
            const int revBase = MaxLResp - 1;
            for (int i = 0; i < lResp; i++)
            {
                impLpcRev[revBase + i] = _impLpcBuf[Order + lResp - i - 1];
            }

            float[] imp = _imp;
            Array.Copy(_impLpcBuf, Order, imp, 0, lResp);
            Array.Clear(_phi, 0, _phi.Length);
            PercFiltMa(impLpcRev, revBase, lResp, imp, lResp, _phi, 0);
            Reverse(_phi, 0, lResp);
            for (int i = lResp; i < fcbSubfrlen; i++)
            {
                _phi[i] = 0.0f;
            }

            Array.Clear(_phiFlip, 0, _phiFlip.Length);
            _phiFlip[MaxSfLen] = _phi[0];
            for (int i = 0; i < lResp + 1; i++)
            {
                _phiFlip[MaxSfLen - i] = _phi[i];
                _phiFlip[MaxSfLen + i] = _phi[i];
            }

            float[] resLpcPad = _resLpcPad;
            Array.Clear(resLpcPad, 0, resLpcPad.Length);
            Array.Copy(resLpc, resLpcPad, fcbSubfrlen);
            float[] dLpc = _dLpc;
            Array.Clear(dLpc, 0, dLpc.Length);
            MultSymtoepl2(_phiFlip, MaxSfLen - lResp + 1, lResp, resLpcPad, 0, dLpc, fcbSubfrlen);

            AcbgParams acbg = _acbg;
            acbg.WerrIn = 0;
            Array.Clear(acbg.PhiAcb, 0, acbg.PhiAcb.Length);
            Array.Clear(acbg.DAcbLpc, 0, acbg.DAcbLpc.Length);
            Array.Clear(acbg.AcbBasisPhi, 0, acbg.AcbBasisPhi.Length);
            float[] zirLpc = _zirLpc;
            Array.Clear(zirLpc, 0, zirLpc.Length);

            if (!_ignoreZir)
            {
                float[] zirTmp = _zirTmp;
                Array.Clear(zirTmp, 0, zirTmp.Length);
                const int zt = MaxLResp - 1;
                float[] htZir = _htZir;
                Array.Clear(htZir, 0, htZir.Length);
                const int ht = MaxLResp - 1;

                int stateLen = Order;
                if (lResp - 1 > stateLen)
                {
                    stateLen = lResp - 1;
                }

                for (int i = 0; i < stateLen; i++)
                {
                    zirTmp[zt - stateLen + i] = _stateWghtBuf[Order + (fcbSubfrlen - stateLen) + i];
                }

                for (int nn = 0; nn < lResp; nn++)
                {
                    float res = zirTmp[zt + nn];
                    for (int i = 0; i < 16; i++)
                    {
                        res -= predcoef[16 - i] * zirTmp[zt + nn - 16 + i];
                    }

                    zirTmp[zt + nn] = res;
                }

                PercFiltMa(zirTmp, zt, lResp, percWghtResp, lResp, zirLpc, 0);
                for (int i = 0; i < lResp; i++)
                {
                    zirTmp[zt + i] = zirLpc[lResp - i - 1];
                }

                for (int i = 0; i < lResp - 1; i++)
                {
                    zirTmp[zt - (lResp - 1) + i] = 0.0f;
                }

                PercFiltMa(zirTmp, zt, lResp, imp, lResp, htZir, ht);
                Reverse(htZir, ht, lResp);

                if (voiced)
                {
                    acbg.WerrIn = DotProd(dLpc, 0, resLpc, 0, fcbSubfrlen)
                        + 2.0f * DotProd(htZir, ht, resLpc, 0, lResp) + Nrg(zirLpc, lResp);
                }

                for (int i = 0; i < lResp; i++)
                {
                    dLpc[i] += htZir[ht + i];
                }
            }
            else if (voiced)
            {
                acbg.WerrIn = DotProd(dLpc, 0, resLpc, 0, fcbSubfrlen);
            }

            float[] acbBasis = _acbBasis;
            float[] acb = _acb;
            float[] dLtp = _dLtp;
            Array.Clear(acbBasis, 0, acbBasis.Length);
            Array.Clear(acb, 0, acb.Length);
            Array.Clear(dLtp, 0, dLtp.Length);
            var o = new CelpSubframeOut();
            short[] acbIdx = o.AcbIdx;
            acbIdx[0] = -1;
            acbIdx[1] = -1;
            var acbGain = new float[AcbgM];

            if (voiced)
            {
                SynLtpBasis(lags, fcbSubfrlen / LagSubfrlen, AcbState, _acbStateLen, acbBasis);
                int idx = CalcAcbGain(lResp, acbBasis, dLpc, acbg, dLtp);
                acbIdx[Main] = (short)idx;
                AcbDequant(_lowRate, acbIdx[Main], acbGain);
                AcbSynthesize(fcbSubfrlen, acbBasis, acbGain, acb);
                acbIdx[Fec] = acbIdx[Main];
            }

            float[] wtgtTmp = _wtgtTmp;
            Array.Clear(wtgtTmp, 0, wtgtTmp.Length);
            const int wt = MaxLResp - 1;
            float[] wtgt = _wtgt;
            Array.Clear(wtgt, 0, wtgt.Length);
            Array.Copy(resLpc, 0, wtgtTmp, wt, fcbSubfrlen);
            if (voiced)
            {
                for (int i = 0; i < fcbSubfrlen; i++)
                {
                    wtgtTmp[wt + i] += -RateAcbScale * acb[i];
                }
            }

            PercFiltMa(wtgtTmp, wt, fcbSubfrlen + lResp, imp, lResp, wtgt, 0);
            for (int i = 0; i < lResp; i++)
            {
                wtgt[i] += zirLpc[i];
            }

            float nrgWtgt = Nrg(wtgt, fcbSubfrlen + lResp);
            var wnrgPerPulse = new float[MaxRates];
            for (int r = 0; r < MaxRates; r++)
            {
                wnrgPerPulse[r] = nrgWtgt / (subfrImportance[r] + 1.0e-3f);
            }

            int iLag = (int)lags[(fcbSubfrlen / LagSubfrlen) - 1];

            short[] nPulses = o.NPulses;
            var gainFromSearch = new float[MaxRates];
            var fcbWnrg = new float[MaxRates];
            var wnrg = new float[MaxRates];
            short[][] pulses = _pulses;
            Array.Clear(pulses[0], 0, MaxPulsesPerSf);
            Array.Clear(pulses[1], 0, MaxPulsesPerSf);

            if (fcbPulsesMax[Main] > 0)
            {
                float[] target = voiced ? dLtp : dLpc;
                bool useGreedy = fcbPulsesMax[Main] - 1 > 0 && surv[fcbPulsesMax[Main] - 2] == 1 && !_lowRate;
                if (useGreedy)
                {
                    FcbSearch(target, wnrgPerPulse, fcbPulsesMax, pulses, nPulses, wnrg, gainFromSearch, fcbWnrg);
                }
                else
                {
                    float ps = _lowRate ? PitchSharpeningCoef : 0.0f;
                    FcbSearchDeldec(target, ps, iLag, wnrgPerPulse, fcbPulsesMax, surv, pulses, nPulses, wnrg, gainFromSearch, fcbWnrg);
                }
            }

            short[] gainIdx = o.GainIdx;
            gainIdx[0] = -1;
            gainIdx[1] = -1;
            float fcbgain = 0;
            float[] excFcb = _excFcb;
            Array.Clear(excFcb, 0, excFcb.Length);
            Tables tbl = TablesInst.Value;
            for (int r = 0; r < MaxRates; r++)
            {
                FcbSynthesize(fcbSubfrlen, pulses[r], nPulses[r], _excFcbRaw);
                Array.Copy(_excFcbRaw, excFcb, fcbSubfrlen);
                if (nPulses[r] > 0)
                {
                    if (voiced)
                    {
                        if (_lowRate)
                        {
                            PitchSharp(excFcb, iLag, fcbSubfrlen);
                        }

                        fcbgain = CalcGainsV(fcbWnrg[r], gainFromSearch[r], excFcb, dLpc, acbg, r, acbIdx, gainIdx);
                    }
                    else
                    {
                        gainIdx[r] = QuantGainUv(gainFromSearch[r]);
                        fcbgain = tbl.FcbgainsUV[gainIdx[r]];
                    }

                    for (int i = 0; i < fcbSubfrlen; i++)
                    {
                        excFcb[i] *= fcbgain;
                    }
                }
            }

            var excLpc = new float[fcbSubfrlen];
            Array.Copy(excFcb, excLpc, fcbSubfrlen);
            if (voiced)
            {
                AcbDequant(_lowRate, acbIdx[Main], acbGain);
                AcbSynthesize(fcbSubfrlen, acbBasis, acbGain, acb);
                for (int i = 0; i < fcbSubfrlen; i++)
                {
                    excLpc[i] += acb[i];
                }
            }

            Array.Copy(AcbState, fcbSubfrlen, AcbState, 0, _acbStateLen - fcbSubfrlen);
            Array.Copy(excLpc, 0, AcbState, _acbStateLen - 2 * fcbSubfrlen, fcbSubfrlen);

            if (!_ignoreZir)
            {
                float[] lpcResErr = _lpcResErr;
                for (int i = 0; i < fcbSubfrlen; i++)
                {
                    lpcResErr[i] = resLpc[i] - excLpc[i];
                }

                Array.Copy(_stateErrLpcSyn, _stateWghtBuf, Order);
                FiltAr16(lpcResErr, fcbSubfrlen, predcoef, Order, _stateWghtBuf);
                Array.Copy(_stateWghtBuf, Order + (fcbSubfrlen - Order), _stateErrLpcSyn, 0, Order);
            }

            _subfrCnt++;
            if (_subfrCnt == _subfrPerPacket)
            {
                for (int r = 0; r < MaxRates; r++)
                {
                    _prevAcbIdx[r] = -1;
                    _prevFcbIdx[r] = -1;
                }

                _subfrCnt = 0;
            }
            else
            {
                for (int r = 0; r < MaxRates; r++)
                {
                    _prevAcbIdx[r] = voiced ? acbIdx[r] : -1;
                    _prevFcbIdx[r] = voiced ? gainIdx[r] : -1;
                }
            }

            for (int r = 0; r < MaxRates; r++)
            {
                int n = nPulses[r] < 0 ? 0 : nPulses[r];
                var pr = new short[n];
                Array.Copy(pulses[r], pr, n);
                o.Pulses[r] = pr;
            }

            o.ExcLpc = excLpc;
            return o;
        }

        /// <summary>Splits tot_surv survivors across pulse counts.</summary>
        public static void SmplDistributeFcbSurv(short[] numsurv, int maxPulses, int totSurv)
        {
            if (maxPulses <= 1)
            {
                numsurv[0] = 1;
                return;
            }

            for (int i = 0; i < maxPulses; i++)
            {
                numsurv[i] = 1;
            }

            int sumSurv = maxPulses;
            int extraSurv = totSurv - maxPulses;
            int extra = extraSurv / (maxPulses - 1);
            if (extra > FcbSrvMax - 1)
            {
                extra = FcbSrvMax - 1;
            }

            for (int i = 0; i < maxPulses - 1; i++)
            {
                numsurv[i] = (short)(numsurv[i] + extra);
            }

            sumSurv += extra * (maxPulses - 1);
            int ix = maxPulses - 2;
            while (sumSurv < totSurv)
            {
                if (numsurv[ix] < FcbSrvMax)
                {
                    numsurv[ix]++;
                    sumSurv++;
                }

                ix--;
                if (ix < 0)
                {
                    break;
                }
            }
        }
    }
}
