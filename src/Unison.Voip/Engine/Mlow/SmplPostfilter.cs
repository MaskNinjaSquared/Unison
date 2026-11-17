// =============================================================================
// SmplPostfilter
//
// Postfilters: the excitation-domain harmonic comb (scaffold, as upstream), the
// post-LPC HP pitch-harmonic comb, and the per-packet harmonic postfilter with
// its 48-sample group delay.
// Ports: meowcaller mlow/postfilter.go (MIT)
// =============================================================================
using System;

namespace Unison.Voip.Engine.Mlow
{
    /// <summary>Excitation-domain comb postfilter state (upstream scaffold).</summary>
    internal sealed class SmplPostfilterState
    {
        public float EnvState;
    }

    /// <summary>Post-LPC HP comb state (C HpPst). LagOld &lt; 0 marks a fresh/reset filter.</summary>
    internal sealed class HpPostfilterState
    {
        internal float StateLoEmph1;
        internal float StateLoEmph2;
        internal float[] StateHp = new float[4];
        internal float LagOld = -1.0f;
        internal float[] XOld = new float[SmplSynth.SmplIntfLen];
        internal float[] CoefMA = new float[3];
        internal float[] CoefAR = new float[3];
    }

    /// <summary>Per-packet harmonic postfilter state (C HarmPst).</summary>
    internal sealed class HarmPostfilterState
    {
        internal readonly float[] State1 = new float[2 * SmplPostfilter.HarmFBDelay];
        internal readonly float[] Lpcoefs = new float[2 * SmplPostfilter.HarmFBDelay + 1];
        internal readonly float[] StateComb = new float[SmplPostfilter.HarmStateCombLen];
        internal int PrevLag;
        internal int PrevDidFilter;
    }

    internal static class SmplPostfilter
    {
        private const float LoEmph1 = -0.995f;

        private const float HpPitchMAF = 0.1f;
        private const float HpDefMAF = 0.1f;
        private const float HpDefFcornerHz = 50.0f;
        private const float LagChangeThreshold = 1.25f;
        private const float HpPostfTransitionSpeed = 2.0f;

        private static readonly float[] HpPitchARF = { 0.608057355f, 0.070939485f };
        private static readonly float[] HpPitchARR = { -2.187380512f, 2.291030664f };
        private static readonly float[] HpDefARF = { 0.728508218f, 0.476039848f };
        private static readonly float[] HpDefARR = { -4.363803713f, 8.441854006f };

        public const int HarmMaxFramesPerPacket = 6;
        public const int HarmMinPitchLag = 32;
        public const int HarmMaxPitchLag = 320;
        public const int HarmMaxpitchLen = 320;
        public const int HarmFBDelay = 8;
        public const int HarmLagSubfrLen = 40;
        public const int HarmDelay = 40;
        public const int HarmPitchNumSubframes = 8;
        private const float HarmFBStrength = 0.4734f;
        private const float HarmStrength = 0.6438f;
        private const float HarmCutoffHz = 4000.0f;
        private const float HarmNHarmCutoff = 6.3f;
        private const float HarmReductionFac = 0.0579f;
        private const int HarmLPFiltRes = 2500;
        public const int HarmStateCombLen = HarmMaxpitchLen + SmplSynth.SmplIntfLen * HarmMaxFramesPerPacket + HarmDelay;
        private const int HarmNumLPFilt = HarmLPFiltRes / 80 - HarmLPFiltRes / HarmMaxPitchLag + 1;

        private static readonly Lazy<float[]> RampDnTab = new Lazy<float[]>(BuildRampDn);
        private static readonly Lazy<float[][]> HarmLpFilters = new Lazy<float[][]>(BuildHarmTables);

        /// <summary>Excitation-domain harmonic comb (func 3524). Upstream is an unimplemented scaffold.</summary>
        public static void SmplCombPostfilter(SmplPostfilterState st, float[] input, int n, bool active, float gain8, float[] nrgEnv, float[] output)
        {
            throw new NotImplementedException("mlow: SmplCombPostfilter not yet implemented (scaffold)");
        }

        // --- post-LPC HP (pitch-harmonic) comb ---

        private static float CosApprox(float x)
        {
            return 1.0f - 0.5f * x * x;
        }

        /// <summary>3-tap FIR with carried 2-sample input history (smpl_filt_ma2 general).</summary>
        public static void SmplPfFir3(float[] input, int n, float[] coef, float[] state, float[] output)
        {
            float xm1 = state[0];
            float xm2 = state[1];
            for (int i = 0; i < n; i++)
            {
                float p1 = i >= 1 ? input[i - 1] : xm1;
                float p2;
                if (i >= 2)
                {
                    p2 = input[i - 2];
                }
                else if (i == 1)
                {
                    p2 = xm1;
                }
                else
                {
                    p2 = xm2;
                }

                output[i] = coef[0] * input[i] + coef[1] * p1 + coef[2] * p2;
            }

            if (n >= 2)
            {
                state[0] = input[n - 1];
                state[1] = input[n - 2];
            }
            else if (n == 1)
            {
                state[1] = xm1;
                state[0] = input[0];
            }
        }

        /// <summary>y[n] = in[n] - c1*y[n-1] - c2*y[n-2], 4-wide unrolled to match C rounding.</summary>
        private static void PfFiltAR2(float[] input, int n, float c1, float c2, float[] state, float[] output)
        {
            float ytmp0 = state[1];
            float ytmp1 = state[0];
            float ar1 = -c1;
            float ar2 = -c2;
            float ar1x2 = ar1 * ar1;
            float ar1x3 = ar1 * ar1x2;
            float ar1x4 = ar1 * ar1x3;
            float imp1 = ar1;
            float imp2 = ar1x2 + ar2;
            float imp3 = ar1x3 + 2.0f * ar1 * ar2;
            float imp4 = ar1x4 + ar2 * ar2 + 3.0f * ar1x2 * ar2;
            float ymp1 = ar2;
            float ymp2 = ar2 * imp1;
            float ymp3 = ar2 * imp2;
            float ymp4 = ar2 * imp3;
            int nn = 0;
            while (nn + 3 < n)
            {
                float xtmp0 = input[nn];
                float xtmp1 = input[nn + 1];
                float xtmp2 = input[nn + 2];
                output[nn + 2] = xtmp2 + imp1 * xtmp1 + imp2 * xtmp0 + imp3 * ytmp1 + ymp3 * ytmp0;
                float xtmp3 = input[nn + 3];
                output[nn + 3] = xtmp3 + imp1 * xtmp2 + imp2 * xtmp1 + imp3 * xtmp0 + imp4 * ytmp1 + ymp4 * ytmp0;
                output[nn] = xtmp0 + imp1 * ytmp1 + ymp1 * ytmp0;
                output[nn + 1] = xtmp1 + imp1 * xtmp0 + imp2 * ytmp1 + ymp2 * ytmp0;
                ytmp0 = output[nn + 2];
                ytmp1 = output[nn + 3];
                nn += 4;
            }

            while (nn < n)
            {
                output[nn] = input[nn] + ar1 * ytmp1 + ar2 * ytmp0;
                ytmp0 = ytmp1;
                ytmp1 = output[nn];
                nn++;
            }

            state[1] = ytmp0;
            state[0] = ytmp1;
        }

        /// <summary>Leaky integrator y[n] = x[n] - c1*y[n-1], 5-wide unrolled to match C rounding.</summary>
        private static void PfFiltAR1(float[] input, int n, float c1, ref float state, float[] output)
        {
            float ar1 = -c1;
            float ar1x2 = ar1 * ar1;
            float ar1x3 = ar1 * ar1x2;
            float ar1x4 = ar1 * ar1x3;
            float ar1x5 = ar1 * ar1x4;
            float ytmp = state;
            int nn = 0;
            while (nn + 4 < n)
            {
                float xtmp0 = input[nn];
                float xtmp1 = input[nn + 1];
                float xtmp2 = input[nn + 2];
                float xtmp3 = input[nn + 3];
                float xtmp4 = input[nn + 4];
                output[nn + 4] = xtmp4 + ar1 * xtmp3 + ar1x2 * xtmp2 + ar1x3 * xtmp1 + ar1x4 * xtmp0 + ar1x5 * ytmp;
                output[nn] = xtmp0 + ar1 * ytmp;
                output[nn + 1] = xtmp1 + ar1 * xtmp0 + ar1x2 * ytmp;
                output[nn + 2] = xtmp2 + ar1 * xtmp1 + ar1x2 * xtmp0 + ar1x3 * ytmp;
                output[nn + 3] = xtmp3 + ar1 * xtmp2 + ar1x2 * xtmp1 + ar1x3 * xtmp0 + ar1x4 * ytmp;
                ytmp = output[nn + 4];
                nn += 5;
            }

            while (nn < n)
            {
                ytmp = input[nn] + ytmp * ar1;
                output[nn] = ytmp;
                nn++;
            }

            state = ytmp;
        }

        /// <summary>y[n] = x[n] + c1*x[n-1] (companion pre-emphasis).</summary>
        private static void PfFiltMA1(float[] input, int n, float c1, ref float state, float[] output)
        {
            float prev = state;
            for (int i = n - 1; i >= 1; i--)
            {
                output[i] = input[i] + c1 * input[i - 1];
            }

            if (n > 0)
            {
                output[0] = input[0] + c1 * prev;
                state = input[n - 1];
            }
        }

        /// <summary>Default fixed-corner ARMA2 biquad for a corner frequency clamped to [5, 1500] Hz.</summary>
        public static void SmplGetHpCoefs(float fcornerHz, out float[] coefMA, out float[] coefAR)
        {
            float fc = fcornerHz;
            if (fc < 5.0f)
            {
                fc = 5.0f;
            }

            if (fc > 1500.0f)
            {
                fc = 1500.0f;
            }

            SmplCalcHpCoefs(HpDefMAF, HpDefARF, HpDefARR, fc / 16000.0f, out coefMA, out coefAR);
        }

        /// <summary>MA2 numerator then AR2 denominator, shared 4-wide state.</summary>
        public static void SmplFiltArma2(float[] input, int n, float[] coefMA, float[] coefAR, float[] state, float[] output)
        {
            var tmp = new float[n];
            var maSt = new[] { state[0], state[1] };
            SmplPfFir3(input, n, coefMA, maSt, tmp);
            state[0] = maSt[0];
            state[1] = maSt[1];
            var arSt = new[] { state[2], state[3] };
            PfFiltAR2(tmp, n, coefAR[1], coefAR[2], arSt, output);
            state[2] = arSt[0];
            state[3] = arSt[1];
        }

        /// <summary>Unity-DC comb biquad: AR resonance at 2*pi*arf*f with radius 1+arr*f, MA scaled for unity DC.</summary>
        private static void SmplCalcHpCoefs(float maf, float[] arf, float[] arr, float f, out float[] coefMA, out float[] coefAR)
        {
            coefMA = new[] { 1.0f, -2.0f * CosApprox(2.0f * SmplSynth.SmplPiF32 * maf * f), 1.0f };
            float far = arf[0] * f + arf[1] * f * f;
            float rar = arr[0] * f + arr[1] * f * f;
            coefAR = new[]
            {
                1.0f,
                -2.0f * CosApprox(2.0f * SmplSynth.SmplPiF32 * far) * (1.0f + rar),
                1.0f + (2.0f * rar + rar * rar),
            };
            float sc = (1.0f - coefAR[1] + coefAR[2]) / (1.0f - coefMA[1] + coefMA[2]);
            coefMA[0] *= sc;
            coefMA[1] *= sc;
            coefMA[2] *= sc;
        }

        /// <summary>Voiced pitch curve when lag&gt;0 (f=1/lag), else the default 50 Hz curve.</summary>
        private static void NewCoefs(HpPostfilterState st, float lag)
        {
            if (lag > 0.0f)
            {
                SmplCalcHpCoefs(HpPitchMAF, HpPitchARF, HpPitchARR, 1.0f / lag, out st.CoefMA, out st.CoefAR);
            }
            else
            {
                SmplCalcHpCoefs(HpDefMAF, HpDefARF, HpDefARR, HpDefFcornerHz / 16000.0f, out st.CoefMA, out st.CoefAR);
            }
        }

        /// <summary>cos(omega)^2 down-ramp for the lag-change overlap-add.</summary>
        private static float[] BuildRampDn()
        {
            float dOmega = SmplSynth.SmplPiF32 / (2.0f * ((float)SmplSynth.SmplIntfLen + 1.0f));
            float omega = dOmega;
            var t = new float[SmplSynth.SmplIntfLen];
            for (int i = 0; i < SmplSynth.SmplIntfLen; i++)
            {
                t[i] = (float)Math.Pow((float)Math.Cos(omega), HpPostfTransitionSpeed);
                omega += dOmega;
            }

            return t;
        }

        /// <summary>Post-LPC HP comb; lag is the frame's average pitch lag (sum(l^2)/sum(l)), 0 for unvoiced.</summary>
        public static void SmplHpPostfilter(HpPostfilterState st, float[] xIn, int n, float lag, float[] output)
        {
            var x = new float[n];
            PfFiltAR1(xIn, n, LoEmph1, ref st.StateLoEmph1, x);

            bool overlap = false;
            var yOld = new float[n];
            if (st.LagOld < 0.0f)
            {
                NewCoefs(st, lag);
                st.LagOld = lag;
            }
            else if (lag > LagChangeThreshold * st.LagOld || LagChangeThreshold * lag < st.LagOld)
            {
                overlap = true;
                SmplFiltArma2(x, n, st.CoefMA, st.CoefAR, st.StateHp, yOld);
                NewCoefs(st, lag);
                st.LagOld = lag;
                var xOld = (float[])st.XOld.Clone();
                var dummy = new float[n];
                SmplFiltArma2(xOld, n, st.CoefMA, st.CoefAR, st.StateHp, dummy);
            }
            else if (lag != st.LagOld)
            {
                NewCoefs(st, lag);
                st.LagOld = lag;
            }

            Array.Copy(x, st.XOld, n);

            var yTmp = new float[n];
            SmplFiltArma2(x, n, st.CoefMA, st.CoefAR, st.StateHp, yTmp);

            if (overlap)
            {
                float[] ramp = RampDnTab.Value;
                for (int i = 0; i < n; i++)
                {
                    yTmp[i] += (yOld[i] - yTmp[i]) * ramp[i];
                }
            }

            PfFiltMA1(yTmp, n, LoEmph1, ref st.StateLoEmph2, output);
        }

        // --- per-packet harmonic postfilter (smpl_harm_postfilter.c) ---

        private static int LagToFiltIx(int lag)
        {
            int d = lag + 30;
            if (d < 80)
            {
                d = 80;
            }

            return HarmLPFiltRes / d - HarmLPFiltRes / HarmMaxPitchLag;
        }

        private static float[][] BuildHarmTables()
        {
            var filtWin = new float[HarmFBDelay];
            float dOmega = (0.5f * SmplSynth.SmplPiF32) / ((float)HarmFBDelay + 1.0f);
            float omega = dOmega;
            for (int i = 0; i < HarmFBDelay; i++)
            {
                filtWin[i] = (float)Math.Cos(omega) / ((float)i + 1.0f);
                omega += dOmega;
            }

            var lp = new float[HarmNumLPFilt][];
            for (int i = 0; i < HarmNumLPFilt; i++)
            {
                lp[i] = new float[2 * HarmFBDelay + 1];
            }

            int ixPrev = -1;
            for (int lag = HarmMinPitchLag; lag <= HarmMaxPitchLag; lag++)
            {
                int ix = LagToFiltIx(lag);
                if (ix != ixPrev)
                {
                    HarmCreateLpFilter((2.0f * SmplSynth.SmplPiF32) / (float)lag, filtWin, lp[ix]);
                    ixPrev = ix;
                }
            }

            return lp;
        }

        private static void HarmCreateLpFilter(float omega0, float[] filtWin, float[] blp)
        {
            float omegaC = omega0 * HarmNHarmCutoff;
            const float lim = HarmCutoffHz / 16000.0f * SmplSynth.SmplPiF32;
            if (lim < omegaC)
            {
                omegaC = lim;
            }

            float sumB = 0;
            float omegaCSum = omegaC;
            for (int i = 0; i < HarmFBDelay; i++)
            {
                float b = filtWin[i] * (float)Math.Sin(omegaCSum);
                omegaCSum += omegaC;
                blp[HarmFBDelay + i + 1] = b;
                blp[HarmFBDelay - i - 1] = b;
                sumB += 2.0f * b;
            }

            blp[HarmFBDelay] = omegaC;
            sumB += omegaC;
            float sc = 1.0f / sumB;
            for (int k = 0; k < blp.Length; k++)
            {
                blp[k] *= sc;
            }
        }

        private static float HarmDotProd(float[] a, int aOff, float[] b, int bOff, int l)
        {
            float r = 0;
            for (int i = 0; i < l; i++)
            {
                r += a[aOff + i] * b[bOff + i];
            }

            return r;
        }

        private static float HarmNrg(float[] x, int off, int n)
        {
            float r = 0;
            for (int i = 0; i < n; i++)
            {
                r += x[off + i] * x[off + i];
            }

            return r;
        }

        /// <summary>17-tap symmetric MA reading 16 samples of history from buf[xBase-16..].</summary>
        private static void HarmFiltMA16Sym(float[] buf, int xBase, int n, float[] coef, float[] output)
        {
            for (int nn = 0; nn < n; nn++)
            {
                int c = xBase + nn;
                float res = buf[c - 8] * coef[8];
                for (int i = 0; i < 8; i++)
                {
                    res += coef[i] * (buf[c - i] + buf[c - 16 + i]);
                }

                output[nn] = res;
            }
        }

        /// <summary>harm_postfilter_core: filters one 40-sample lag block.</summary>
        private static void HarmPostfilterCore(
            float[] lpcoefs,
            float[] comb,
            int combX,
            int futureSamples,
            int lag,
            float[] diff,
            int diffBase,
            float[] output,
            int outOff,
            int l,
            float fbStrength,
            ref int prevDidFilter)
        {
            float[][] lpFilters = HarmLpFilters.Value;
            float xy = 0;
            if (lag > 0)
            {
                int lookforward = l + lag - futureSamples;
                if (lookforward > 0)
                {
                    int l2 = l - lookforward;
                    if (l2 < 0)
                    {
                        l2 = 0;
                    }

                    for (int i = 0; i < l2; i++)
                    {
                        output[outOff + i] = comb[combX + i - lag] + comb[combX + i + lag];
                    }

                    for (int i = 0; i < l - l2; i++)
                    {
                        output[outOff + l2 + i] = comb[combX + l2 + i - lag] + comb[combX + l2 + i];
                    }
                }
                else
                {
                    for (int i = 0; i < l; i++)
                    {
                        output[outOff + i] = comb[combX + i - lag] + comb[combX + i + lag];
                    }
                }

                xy = HarmDotProd(comb, combX, output, outOff, l);
            }

            if (lag > 0 && xy > 0.0f)
            {
                float xx = HarmNrg(comb, combX, l);
                float yy = 0.25f * HarmNrg(output, outOff, l);
                float denom = yy;
                if (xx > denom)
                {
                    denom = xx;
                }

                float strength = 0.5f * xy / denom;
                float highLagReduction = 1.0f - HarmReductionFac * ((float)(lag - HarmMinPitchLag) / (float)(HarmMaxPitchLag - HarmMinPitchLag));
                strength = strength * highLagReduction * HarmStrength;
                for (int i = 0; i < l; i++)
                {
                    output[outOff + i] *= 0.5f * strength;
                }

                for (int i = 0; i < l; i++)
                {
                    diff[diffBase + i] = output[outOff + i] + (-strength) * comb[combX + i];
                }

                float[] kernel = lpFilters[LagToFiltIx(lag)];
                for (int k = 0; k < 2 * HarmFBDelay + 1; k++)
                {
                    lpcoefs[k] = kernel[k] * fbStrength;
                }

                var coef17 = (float[])lpcoefs.Clone();
                var yh = new float[HarmLagSubfrLen];
                HarmFiltMA16Sym(diff, diffBase, l, coef17, yh);
                for (int i = 0; i < l; i++)
                {
                    output[outOff + i] = yh[i] + comb[combX - HarmFBDelay + i];
                }

                prevDidFilter = 1;
            }
            else
            {
                for (int i = 0; i < HarmLagSubfrLen; i++)
                {
                    diff[diffBase + i] = 0.0f;
                }

                if (prevDidFilter != 0)
                {
                    var coef17 = (float[])lpcoefs.Clone();
                    var yh = new float[2 * HarmFBDelay];
                    HarmFiltMA16Sym(diff, diffBase, 2 * HarmFBDelay, coef17, yh);
                    for (int i = 0; i < 2 * HarmFBDelay; i++)
                    {
                        output[outOff + i] = yh[i] + comb[combX - HarmFBDelay + i];
                    }

                    for (int i = 2 * HarmFBDelay; i < l; i++)
                    {
                        output[outOff + i] = comb[combX + HarmFBDelay + i - 2 * HarmFBDelay];
                    }
                }
                else
                {
                    for (int i = 0; i < l; i++)
                    {
                        output[outOff + i] = comb[combX - HarmFBDelay + i];
                    }
                }

                prevDidFilter = 0;
            }
        }

        /// <summary>
        /// Harmonic postfilter over a full packet, IN PLACE. lags are the per-40-block lags
        /// (nLags = packetlen/40); normalizedBitrate is the packet average.
        /// </summary>
        public static void SmplHarmPostfilter(HarmPostfilterState st, float[] x, int xLen, float[] lags, int nLags, float normalizedBitrate)
        {
            const int diffPrefix = 2 * HarmFBDelay;
            var diff = new float[SmplSynth.SmplIntfLen + diffPrefix];

            int lag = st.PrevLag;
            const int combCur = HarmMaxPitchLag + HarmDelay;
            Array.Copy(x, 0, st.StateComb, combCur, xLen);

            float fbStrength = 1.0f - HarmFBStrength * normalizedBitrate;
            int offset1 = 0;
            int lagCtr = 0;
            while (lagCtr < nLags)
            {
                int offset2 = 0;
                Array.Copy(st.State1, 0, diff, diffPrefix - 16, 16);
                int lagCtrEnd = lagCtr + HarmPitchNumSubframes;
                if (lagCtrEnd > nLags)
                {
                    lagCtrEnd = nLags;
                }

                while (lagCtr < lagCtrEnd)
                {
                    int combX = HarmMaxPitchLag + offset1;
                    int futureSamples = HarmDelay + xLen - offset1;
                    HarmPostfilterCore(st.Lpcoefs, st.StateComb, combX, futureSamples, lag, diff, diffPrefix + offset2, x, offset1, HarmLagSubfrLen, fbStrength, ref st.PrevDidFilter);
                    offset1 += HarmLagSubfrLen;
                    offset2 += HarmLagSubfrLen;
                    lag = (int)MlowMath.GoRound(lags[lagCtr]);
                    lagCtr++;
                }

                Array.Copy(diff, diffPrefix + offset2 - 16, st.State1, 0, 16);
            }

            st.PrevLag = lag;
            Array.Copy(st.StateComb, xLen, st.StateComb, 0, combCur);
        }
    }
}
