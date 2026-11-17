// =============================================================================
// CelpDec
//
// Decoder-side CELP synthesis in the codec's native float domain: excitation ->
// ACB/LTP decode -> gen_noise -> LPC synthesis, LSF interpolation, FCB gain
// tables and the post-LPC HP postfilter. Output is float in [-1, 1].
// Ports: meowcaller mlow/celpdec.go (MIT)
// =============================================================================
using System;
using System.Collections.Generic;

namespace Unison.Voip.Engine.Mlow
{
    /// <summary>Per-subframe decoded parameters the synthesis consumes.</summary>
    internal sealed class CelpDecParams
    {
        public bool Voiced;
        public readonly int[] SfPulses = new int[SmplSynth.SmplSubfrCount];
        public readonly int[] FcbgIdx = new int[SmplSynth.SmplSubfrCount];
        public readonly int[] NrgresDbqQ14 = new int[SmplSynth.SmplSubfrCount];
        public readonly int[] AcbgIdx = new int[SmplSynth.SmplSubfrCount];

        /// <summary>Per-40-block pitch lag (codec units), 0 for unvoiced.</summary>
        public readonly float[] BlockLags = new float[2 * SmplSynth.SmplSubfrCount];

        public int TotalPulses;
    }

    /// <summary>Persistent decoder synthesis state (C float domain).</summary>
    internal sealed class CelpDecState
    {
        private const int Order = SmplSynth.SmplOrder;

        private readonly NoiseGenerator _noise = new NoiseGenerator();
        private readonly float[] _acbState;
        private readonly int _acbStateLen;
        private readonly float[] _lpcSynthMem = new float[Order];
        private readonly float[] _uvPulseShapingState = new float[2];
        private readonly HpPostfilterState _hp = new HpPostfilterState();
        private float _tiltPostfilter;

        public CelpDecState()
        {
            _acbStateLen = 160 + 2 * CelpDec.MaxPitchLag + CelpDec.LtpInterpolDelay;
            _acbState = new float[_acbStateLen];
        }

        internal readonly float[] LsfPrev = new float[Order];

        /// <summary>Last unvoiced subframe residual energy; carried like the reference, never read on decode.</summary>
        internal float PrevNrgres;

        /// <summary>When set, SynthFrame captures the per-subframe pre-noise excitation into ExcPre (KAT only).</summary>
        internal bool TraceExcPre;

        internal readonly List<float> ExcPre = new List<float>();

        /// <summary>
        /// Synthesizes one 20 ms internal frame into 320 float samples. nlsf is the
        /// reconstructed order-16 NLSF; pulses are the signed FCB pulse magnitudes
        /// (320 positions); lowRate is the TOC bit.
        /// </summary>
        public void SynthFrame(float[] nlsf, int lsfInterpolIdx, int[] pulses, CelpDecParams p, bool lowRate, int frameLength16, float[] output)
        {
            CelpDec.FcbGainTables gains = CelpDec.FcbGains();
            var a = new float[SmplSynth.SmplSubfrCount][];
            var lsfs = new float[SmplSynth.SmplSubfrCount][];
            for (int i = 0; i < SmplSynth.SmplSubfrCount; i++)
            {
                a[i] = new float[Order + 1];
                lsfs[i] = new float[Order];
            }

            int idx = lsfInterpolIdx > 1 ? 1 : lsfInterpolIdx;
            int numSubframes = SmplSynth.SmplSubfrCount;
            int subframeLen = SmplSynth.SmplSubfrLen;
            float[] interpol = CelpDec.LsfInterpol4[idx];
            if (lowRate)
            {
                numSubframes = 2;
                subframeLen = SmplSynth.SmplIntfLen / numSubframes;
                interpol = CelpDec.LsfInterpol2[idx];
            }

            CelpDec.LpcInterpol(nlsf, LsfPrev, interpol, numSubframes, a, lsfs);

            float normBr = SmplNoise.SmplGetNormalizedBitrate(p.TotalPulses, frameLength16);

            var lpcRes = new float[SmplSynth.SmplIntfLen];
            float[] gainTab = p.Voiced ? gains.V : gains.UV;
            for (int pos = 0; pos < SmplSynth.SmplIntfLen; pos++)
            {
                if (pulses[pos] != 0)
                {
                    int sf = pos / subframeLen;
                    lpcRes[pos] = (float)pulses[pos] * gainTab[p.FcbgIdx[sf]];
                }
            }

            int lagsPerSubfr = subframeLen / CelpDec.LagSubfrLen;
            int acbStateLen = subframeLen + 2 * CelpDec.MaxPitchLag + CelpDec.LtpInterpolDelay;
            var ybuf = new float[Order + SmplSynth.SmplIntfLen];
            Array.Copy(_lpcSynthMem, ybuf, Order);
            if (TraceExcPre)
            {
                ExcPre.Clear();
            }

            var noise = new float[160];
            for (int sf = 0; sf < numSubframes; sf++)
            {
                int b = sf * subframeLen;
                int lagBase = sf * lagsPerSubfr;
                CelpDec.CelpDecode(_acbState, acbStateLen, p.Voiced, p.AcbgIdx[sf], p.BlockLags, lagBase, lagsPerSubfr, subframeLen, lowRate, normBr, lpcRes, b);

                if (TraceExcPre)
                {
                    for (int i = 0; i < subframeLen; i++)
                    {
                        ExcPre.Add(lpcRes[b + i]);
                    }
                }

                if (lowRate && !p.Voiced && p.SfPulses[sf] > 0)
                {
                    CelpDec.SmplLowRateUvPulseShape(lpcRes, b, subframeLen, _uvPulseShapingState);
                }
                else
                {
                    _uvPulseShapingState[0] = 0;
                    _uvPulseShapingState[1] = 0;
                }

                float nrgres = SmplNoise.SmplDecodeResnrg(p.NrgresDbqQ14[sf], subframeLen);
                if (!p.Voiced)
                {
                    PrevNrgres = nrgres;
                }

                Array.Clear(noise, 0, noise.Length);
                SmplNoise.SmplCelpGenNoise(_noise, lpcRes, b, subframeLen, p.Voiced, p.SfPulses[sf], nrgres, p.FcbgIdx[sf], lsfs[sf], normBr, gains.UV, noise);
                for (int i = 0; i < subframeLen; i++)
                {
                    lpcRes[b + i] += noise[i];
                }

                if (lowRate && p.Voiced)
                {
                    CelpDec.SmplLowRateTilt(lpcRes, b, subframeLen, ref _tiltPostfilter);
                }
                else
                {
                    _tiltPostfilter = lpcRes[b + subframeLen - 1];
                }

                CelpDec.FiltAR16(lpcRes, b, a[sf], ybuf, Order + b, subframeLen);
            }

            Array.Copy(ybuf, Order, output, 0, SmplSynth.SmplIntfLen);
            Array.Copy(ybuf, Order + SmplSynth.SmplIntfLen - Order, _lpcSynthMem, 0, Order);

            float lag = 0;
            if (p.Voiced)
            {
                float sl = 0, sll = 0;
                foreach (float l in p.BlockLags)
                {
                    sl += l;
                    sll += l * l;
                }

                if (sl > 0.0f)
                {
                    lag = sll / sl;
                }
            }

            var hpOut = new float[SmplSynth.SmplIntfLen];
            SmplPostfilter.SmplHpPostfilter(_hp, output, SmplSynth.SmplIntfLen, lag, hpOut);
            Array.Copy(hpOut, output, SmplSynth.SmplIntfLen);
        }
    }

    internal static class CelpDec
    {
        public const int LagSubfrLen = 40;
        public const int LtpInterpolDelay = 8;
        public const int MaxPitchLag = 320;
        private const int AcbgM = CelpTables.AcbgM;
        private const float PitchSharpCoef = 0.9881f;
        private const float VGainMinDB = -100.0f;
        private const float VGainStepDB = 3.0f;
        private const float UvGainMinDB = -90.0f;
        private const float UvGainStepDB = 1.0f;

        /// <summary>ACB high-boost endpoints (smpl_dec_acb_high_boost).</summary>
        private static readonly float[] DecAcbHighBoost = { 0.35f, 0.18f };

        /// <summary>LSF-&gt;LPC interpolation factors per subframe, [lsf_interpol_idx][sf].</summary>
        internal static readonly float[][] LsfInterpol4 =
        {
            new[] { 0.55f, 0.88f, 1.0f, 1.0f },
            new[] { 0.3f, 0.65f, 0.95f, 1.0f },
        };

        /// <summary>Low-rate, two-subframe LSF interpolation table.</summary>
        internal static readonly float[][] LsfInterpol2 =
        {
            new[] { 0.75f, 1.0f },
            new[] { 0.4f, 0.95f },
        };

        /// <summary>16-tap symmetric LTP interpolation kernel.</summary>
        internal static readonly float[] InterpolKernel =
        {
            -6.3925986e-6f, 0.00011064114f, -0.0009153038f, 0.00484772f, -0.018698348f, 0.05759091f, -0.15997477f, 0.6170455f,
            0.61704546f, -0.15997475f, 0.057590906f, -0.018698348f, 0.00484772f, -0.0009153038f, 0.000110641144f, -6.392598e-6f,
        };

        internal sealed class FcbGainTables
        {
            public readonly float[] UV = new float[CelpTables.UvGainIdxLen + 1];
            public readonly float[] V = new float[CelpTables.FcbgVN];
        }

        private static readonly Lazy<FcbGainTables> Gains = new Lazy<FcbGainTables>(BuildFcbGains);

        internal static FcbGainTables FcbGains()
        {
            return Gains.Value;
        }

        private static FcbGainTables BuildFcbGains()
        {
            var g = new FcbGainTables();
            for (int ix = 0; ix <= CelpTables.UvGainIdxLen; ix++)
            {
                g.UV[ix] = (float)Math.Pow(10, 0.05f * ((float)ix * UvGainStepDB + UvGainMinDB));
            }

            for (int ix = 0; ix < CelpTables.FcbgVN; ix++)
            {
                g.V[ix] = (float)Math.Pow(10, 0.05f * ((float)ix * VGainStepDB + VGainMinDB));
            }

            return g;
        }

        private static float CelpDot(float[] a, int aOff, float[] b, int l)
        {
            float r = 0;
            for (int i = 0; i < l; i++)
            {
                r += a[aOff + i] * b[i];
            }

            return r;
        }

        /// <summary>
        /// Per-subframe interpolation between prevLsf and lsf, then NLSF-&gt;A. prevLsf is
        /// left at the last interpolated LSF (carried across frames).
        /// </summary>
        internal static void LpcInterpol(float[] lsf, float[] prevLsf, float[] interpol, int numSubframes, float[][] aOut, float[][] lsfsOut)
        {
            const int order = SmplSynth.SmplOrder;
            if (prevLsf[order - 1] == 0.0f)
            {
                Array.Copy(lsf, prevLsf, order);
            }

            var ilsf = new float[order];
            float prevFactor = -1.0f;
            for (int j = 0; j < numSubframes; j++)
            {
                if (interpol[j] == prevFactor)
                {
                    Array.Copy(aOut[j - 1], aOut[j], order + 1);
                }
                else
                {
                    if (interpol[j] == 1.0f)
                    {
                        Array.Copy(lsf, ilsf, order);
                    }
                    else
                    {
                        for (int k = 0; k < order; k++)
                        {
                            ilsf[k] = prevLsf[k] * (1.0f - interpol[j]) + lsf[k] * interpol[j];
                        }
                    }

                    Array.Copy(SmplSynth.SmplNLSF2A(ilsf), aOut[j], order + 1);
                }

                prevFactor = interpol[j];
                Array.Copy(ilsf, lsfsOut[j], order);
            }

            Array.Copy(ilsf, prevLsf, order);
        }

        private static void AcbDequant(bool lowRate, int acbIdx, float[] acbG)
        {
            short[] cb = lowRate ? CelpTables.CbAcbgainsLRQ14 : CelpTables.CbAcbgainsHRQ14;
            const float sc = 1.0f / (1 << 14);
            for (int m = 0; m < AcbgM; m++)
            {
                acbG[m] = (float)cb[acbIdx * AcbgM + m] * sc;
            }
        }

        /// <summary>adjust_acbgains (high-boost) then 3-tap symmetric ACB synthesis.</summary>
        private static void AcbSynthesize(int fcbSubfrlen, float[] acbBasis, float[] acbGIn, float highBoost, float[] acb)
        {
            float g0 = acbGIn[0];
            float g1 = acbGIn[1];
            if (highBoost != 0.0f)
            {
                float f0 = g0 + 2.0f * g1;
                float f1 = g0 - g1;
                float absF2new = MlowMath.MinF32(MlowMath.AbsF32(f1) + highBoost, MlowMath.AbsF32(f0));
                f1 = f1 * (absF2new / (MlowMath.AbsF32(f1) + 1e-12f));
                g0 = (f0 + 2.0f * f1) / 3.0f;
                g1 = (f0 - f1) / 3.0f;
            }

            for (int i = 0; i < fcbSubfrlen; i++)
            {
                acb[i] = g0 * acbBasis[i];
            }

            for (int i = 0; i < fcbSubfrlen; i++)
            {
                acb[i] += g1 * acbBasis[fcbSubfrlen + i];
            }
        }

        private static void PitchSharp(float[] x, int off, int lag, int l)
        {
            for (int i = lag; i < l; i++)
            {
                x[off + i] += x[off + i - lag] * PitchSharpCoef;
            }
        }

        /// <summary>Builds the ACB basis from the excitation history; mutates state forward.</summary>
        private static void SynLtpBasis(float[] lags, int lagOff, int nLags, float[] state, int stateLen, float[] acbBasis)
        {
            int p = stateLen - nLags * LagSubfrLen;
            for (int subfr = 0; subfr < nLags; subfr++)
            {
                float lagV = lags[lagOff + subfr];
                int iLag = (int)Math.Floor(lagV);
                if ((float)iLag == lagV)
                {
                    int il = iLag;
                    for (int i = 0; i < LagSubfrLen; i++)
                    {
                        state[p + i] = state[p + i - il];
                    }

                    for (int i = 0; i < LagSubfrLen; i++)
                    {
                        acbBasis[subfr * LagSubfrLen + i] = state[p + i];
                    }

                    for (int i = 0; i < LagSubfrLen; i++)
                    {
                        acbBasis[(nLags + subfr) * LagSubfrLen + i] = state[p + i - il - 1] + state[p + i - il + 1];
                    }
                }
                else
                {
                    int il = iLag;
                    int baseFirst = p + (-1 - il - LtpInterpolDelay);
                    float first = CelpDot(state, baseFirst, InterpolKernel, 2 * LtpInterpolDelay);
                    int srcBase = p + (-il - LtpInterpolDelay);
                    for (int nn = 0; nn < LagSubfrLen; nn++)
                    {
                        float ret = 0;
                        for (int i = 0; i < 8; i++)
                        {
                            float s0 = state[srcBase + nn + i];
                            float s1 = state[srcBase + nn + 15 - i];
                            ret += (s0 + s1) * InterpolKernel[i];
                        }

                        state[p + nn] = ret;
                    }

                    int baseLast = p + (LagSubfrLen - il - LtpInterpolDelay);
                    float last = CelpDot(state, baseLast, InterpolKernel, 2 * LtpInterpolDelay);
                    for (int i = 0; i < LagSubfrLen; i++)
                    {
                        acbBasis[subfr * LagSubfrLen + i] = state[p + i];
                    }

                    int b1 = (nLags + subfr) * LagSubfrLen;
                    acbBasis[b1] = first + state[p + 1];
                    for (int i = 0; i < LagSubfrLen - 2; i++)
                    {
                        acbBasis[b1 + 1 + i] = state[p + i] + state[p + i + 2];
                    }

                    const int iLast = LagSubfrLen - 1;
                    acbBasis[b1 + iLast] = state[p + iLast - 1] + last;
                }

                p += LagSubfrLen;
            }
        }

        /// <summary>Adds the ACB (LTP) contribution into lpcRes (voiced), then pushes the subframe into the ACB state.</summary>
        internal static void CelpDecode(
            float[] acbState,
            int acbStateLen,
            bool voiced,
            int acbGainIdx,
            float[] lags,
            int lagOff,
            int numLags,
            int subfrlen,
            bool lowRate,
            float normalizedBitrate,
            float[] lpcRes,
            int resOff)
        {
            if (voiced)
            {
                float highBoost = DecAcbHighBoost[0] + (DecAcbHighBoost[1] - DecAcbHighBoost[0]) * normalizedBitrate;
                int iLag = (int)lags[lagOff + numLags - 1];
                if (lowRate)
                {
                    PitchSharp(lpcRes, resOff, iLag, subfrlen);
                }

                var acbBasis = new float[subfrlen * AcbgM];
                var acb = new float[subfrlen];
                SynLtpBasis(lags, lagOff, numLags, acbState, acbStateLen, acbBasis);
                var acbGain = new float[AcbgM];
                AcbDequant(lowRate, acbGainIdx, acbGain);
                AcbSynthesize(subfrlen, acbBasis, acbGain, highBoost, acb);
                for (int i = 0; i < subfrlen; i++)
                {
                    lpcRes[resOff + i] += acb[i];
                }
            }

            Array.Copy(acbState, subfrlen, acbState, 0, acbStateLen - 2 * subfrlen);
            Array.Copy(lpcRes, resOff, acbState, acbStateLen - 2 * subfrlen, subfrlen);
        }

        /// <summary>y[n] = x[n] - sum_i a[16-i]*y[n-16+i]; ybuf holds a 16-sample history at ybuf[base-16..base].</summary>
        internal static void FiltAR16(float[] x, int xOff, float[] a, float[] ybuf, int b, int n)
        {
            const int order = SmplSynth.SmplOrder;
            for (int nn = 0; nn < n; nn++)
            {
                float res = x[xOff + nn];
                for (int i = 0; i < order; i++)
                {
                    res -= a[order - i] * ybuf[b + nn - order + i];
                }

                ybuf[b + nn] = res;
            }
        }

        internal static void SmplLowRateUvPulseShape(float[] x, int off, int n, float[] state)
        {
            float prevX = state[0];
            float prevY = state[1];
            for (int i = 0; i < n; i++)
            {
                float sample = x[off + i];
                float ma = 0.5f * sample + 0.1665f * prevX;
                prevX = sample;
                prevY = ma + 0.333f * prevY;
                x[off + i] = prevY;
            }

            state[0] = prevX;
            state[1] = prevY;
        }

        internal static void SmplLowRateTilt(float[] x, int off, int n, ref float state)
        {
            float prev = state;
            for (int i = 0; i < n; i++)
            {
                float sample = x[off + i];
                x[off + i] = 0.84f * sample + 0.16f * prev;
                prev = sample;
            }

            state = prev;
        }
    }
}
