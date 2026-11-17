// =============================================================================
// SmplSynth
//
// Low-band synthesis: NLSF reconstruction, NLSF->LPC, gain linearization,
// LTP/ACB excitation prediction, the WASM-domain internal-frame synthesis and
// the unvoiced residual-energy quantizer.
// Ports: meowcaller mlow/synth.go (MIT)
// =============================================================================
using System;

namespace Unison.Voip.Engine.Mlow
{
    /// <summary>Runtime synthesis table set (smpl_synth_tables dump).</summary>
    internal sealed class SmplSynthTables
    {
        /// <summary>[stage1][config][grid][coeff][sym].</summary>
        public float[][][][][] Valtables;

        /// <summary>[stage1][grid][16].</summary>
        public float[][][] Centroids;

        /// <summary>[stage1][grid][row][col].</summary>
        public float[][][][] Matrices;

        /// <summary>[stage1][17].</summary>
        public float[][] MinSpacing;

        public float[][] Grid16W;
        public float[] Grid16Alpha;

        /// <summary>[sig][config][256].</summary>
        public float[][][] Grid16Matrices;
    }

    /// <summary>2-tap excitation-gain smoother state.</summary>
    internal sealed class SmplExcGainState
    {
        public float S0;
        public float S1;
    }

    /// <summary>Per-internal-frame pitch synthesis inputs.</summary>
    internal sealed class SmplPitchSynth
    {
        public bool Voiced;
        public readonly double[] LagSubfr = new double[4];
        public double NormGain;
    }

    /// <summary>
    /// Cross-internal-frame low-band synthesis state (LPC state, LTP history, gain smoother).
    /// The reference's gated Region-1 / HP postfilter blocks are not carried here.
    /// </summary>
    internal sealed class SmplFrameSynth
    {
        internal readonly float[] LpcState = new float[SmplSynth.SmplOrder];
        internal readonly float[] LtpHist = new float[SmplSynth.LtpHistLen];
        internal SmplExcGainState Gst = new SmplExcGainState();
    }

    /// <summary>Quantized residual energy; DbqQ14 is what the decoder reads as gainQ.</summary>
    internal sealed class NrgResQuant
    {
        public int FrameQi;
        public int ShapeQi;
        public readonly int[] DbqQ14 = new int[4];
    }

    internal static class SmplSynth
    {
        public const int SmplOrder = 16;
        public const int SmplSubfrLen = 80;
        public const int SmplIntfLen = 320;
        public const int SmplSubfrCount = 4;
        public const int SmplLtpHist = 728;

        internal const float SmplPiF32 = 3.1415927410125f;
        private const float NlsfWeightWMax = 999.9999f;
        private const float NlsfWeightEps = 0.0009999999f;
        private const int StabilizeMaxLoop = 1000;
        private const float StabilizeEps = 9.5367431640625e-07f;

        private const float GLtp = 0.949999988079071f;
        private const int FracStateLen = 728;
        internal const int LtpHistLen = SmplLtpHist + SmplIntfLen + 64;

        internal const float SmplResNrgBias = 3.1622776e-9f;
        private const float ResNrgMinDB = -85.0f;
        private const float ResNrgMaxDB = 0.0f;
        private const int NrgStepDBQ14x4 = 16686;
        private const int ResNrgShapeCBN4 = 98;

        /// <summary>16-tap symmetric fractional-delay interpolation FIR (WASM mem 0xe8780).</summary>
        private static readonly float[] Fir16 =
        {
            -0.000006392598606907995f,
            0.00011064113641623408f,
            -0.0009153038263320923f,
            0.0048477197997272015f,
            -0.018698347732424736f,
            0.05759090930223465f,
            -0.15997476875782013f,
            0.617045521736145f,
            0.6170454621315002f,
            -0.15997475385665894f,
            0.05759090557694435f,
            -0.018698347732424736f,
            0.0048477197997272015f,
            -0.0009153038263320923f,
            0.00011064114369219169f,
            -0.0000063925981521606445f,
        };

        /// <summary>nrgres_shape_CB_4_Q10 (98 vectors x 4 subframes), verbatim.</summary>
        private static readonly short[] NrgresShapeCB4Q10 =
        {
            -2515, -2238, 2632, 2121, 790, 3973, -2872, -1891, -533, 2847, 1453, -3767, -6174, -402, 2668, 3908,
            -1623, -1458, 153, 2928, -1254, 3197, -476, -1467, 1803, -1086, 270, -987, 1952, -66, -1257, -629,
            161, 19, -85, -96, 4833, 3147, -105, -7875, -1320, 1377, -1156, 1099, 3398, -2247, 1485, -2637,
            -3031, 2756, 1841, -1566, -1487, 2202, -2668, 1954, 5518, -5344, 522, -696, 8400, -3123, -6235, 958,
            5152, -2444, -2811, 102, 2513, -82, 1181, -3612, -561, -197, -1074, 1832, -294, -1250, -1839, 3383,
            5126, 522, -782, -4866, -7760, -5178, -1840, 14779, -1119, 6007, -1489, -3399, -4567, -2543, 1855, 5255,
            53, -1626, 67, 1506, -12256, -7706, -1982, 21943, 3549, -969, -1096, -1484, -10824, 2981, 2204, 5639,
            -229, 1106, 945, -1821, -9237, 10157, 1616, -2537, 4916, -199, -2177, -2540, 6673, 984, -3355, -4302,
            -7130, -4677, 8925, 2882, 445, 2762, -348, -2859, -196, -1859, 1761, 294, 2725, -2093, -966, 334,
            -3908, -308, 3675, 541, 735, 890, -2516, 891, 504, 1631, -1157, -977, -17817, 2119, 7104, 8594,
            -2056, 1897, -198, 356, 292, -4544, -287, 4538, -1455, -304, 603, 1156, -18259, -12643, 15247, 15655,
            4177, 1778, -1815, -4140, 1425, 576, -294, -1707, -1301, 5132, 2838, -6669, -4727, -3148, -905, 8781,
            -650, 152, -4654, 5152, 13746, 2320, -6259, -9807, -1356, 396, 3789, -2829, 2337, 1947, -29, -4256,
            6033, 820, -5730, -1123, -1795, 1091, 1080, -377, 2208, -1921, -3314, 3027, 9688, 5218, -3754, -11152,
            3814, -3941, -6183, 6310, -1017, -2391, 4393, -984, 10944, -1182, -5011, -4751, -4640, 7201, -218, -2343,
            -1278, 4720, -4212, 770, 2777, 1333, -5944, 1833, -16066, 8107, 5165, 2795, 2530, -5020, 6073, -3582,
            -2111, -7534, 4575, 5070, -8702, -3762, 4050, 8414, 1335, -997, -1567, 1229, 9348, 1534, -3959, -6922,
            2440, 1153, -2175, -1418, -2715, -4538, -4478, 11730, 569, -885, 2032, -1716, 3529, -91, -3218, -219,
            2157, -4121, 191, 1772, -2123, -1968, -1355, 5446, 1475, -354, 3651, -4772, 1654, -3521, 2726, -859,
            2393, 6820, -2958, -6255, -3861, 1365, 1177, 1319, 7614, -1638, -2789, -3187, -3628, -2635, 6902, -639,
            1925, 2295, -1451, -2769, -3683, 4517, -981, 147, -1260, -529, 2339, -550, 3013, 639, -1050, -2602,
            3651, 1959, -3218, -2391, 6267, 3124, -2926, -6464, -8180, 3900, 4191, 89, -3372, -611, 1042, 2941,
            -2510, 856, -925, 2579, -11667, -8436, 10605, 9498, 6427, -2733, 1887, -5581, 1581, -1722, -328, 469,
            2011, 1989, -3606, -394, -1014, 2197, -1200, 17, 1544, -2555, 765, 247, 1188, -183, 1966, -2972,
            -6057, 3480, -2284, 4860, -25659, 8466, 8891, 8303,
        };

        public static SmplSynthTables LoadSmplSynthTables()
        {
            return LsfSeed.LoadLsfBuilt().Synth;
        }

        private static float LaroiaClamp(float gap)
        {
            return gap > NlsfWeightEps ? 1.0f / gap : NlsfWeightWMax;
        }

        /// <summary>silk_NLSF_VQ_weights_laroia: w[k] = invgap[k] + invgap[k+1].</summary>
        internal static void SmplNlsfLaroiaWeights(float[] nlsf, float[] o)
        {
            var inv = new float[SmplOrder + 1];
            inv[0] = LaroiaClamp(nlsf[0]);
            float prev = nlsf[0];
            for (int k = 1; k < SmplOrder; k++)
            {
                inv[k] = LaroiaClamp(nlsf[k] - prev);
                prev = nlsf[k];
            }

            inv[SmplOrder] = LaroiaClamp(SmplPiF32 - nlsf[SmplOrder - 1]);
            for (int k = 0; k < SmplOrder; k++)
            {
                o[k] = inv[k] + inv[k + 1];
            }
        }

        /// <summary>out[r] = sum_c mat[c*16 + r] * vec[c] (column-major decorrelation matrix).</summary>
        internal static void SmplNlsfDecorr(float[] mat, float[] vec, float[] o)
        {
            var scr = new float[SmplOrder];
            float v0 = vec[0];
            for (int r = 0; r < SmplOrder; r++)
            {
                scr[r] = v0 * mat[r];
            }

            for (int c = 1; c < SmplOrder; c++)
            {
                float v = vec[c];
                int b = c * SmplOrder;
                for (int r = 0; r < SmplOrder; r++)
                {
                    scr[r] += mat[b + r] * v;
                }
            }

            Array.Copy(scr, o, SmplOrder);
        }

        private static void ArgMin(float[] marg, out float m, out int idx)
        {
            m = marg[0];
            idx = 0;
            for (int i = 1; i < SmplOrder + 1; i++)
            {
                if (marg[i] < m)
                {
                    m = marg[i];
                    idx = i;
                }
            }
        }

        /// <summary>silk_NLSF_stabilize: minimum spacing + ordering in the margin domain.</summary>
        internal static void SmplStabilizeNlsf(float[] nlsf, float[] minSpacing)
        {
            const int L = SmplOrder;
            var marg = new float[L + 1];
            marg[0] = nlsf[0] - minSpacing[0];
            for (int i = 1; i < L; i++)
            {
                marg[i] = nlsf[i] - nlsf[i - 1] - minSpacing[i];
            }

            marg[L] = SmplPiF32 - nlsf[L - 1] - minSpacing[L];
            float min;
            int sel;
            ArgMin(marg, out min, out sel);
            int loopN = 0;
            while (min < 0.0f)
            {
                float d = (float)loopN * StabilizeEps - min;
                if (sel == 0)
                {
                    marg[0] += d;
                    marg[1] -= d;
                }
                else if (sel == L)
                {
                    marg[L] += d;
                    marg[L - 1] -= d;
                }
                else
                {
                    marg[sel] += d;
                    float half = d * 0.5f;
                    marg[sel - 1] -= half;
                    marg[sel + 1] -= half;
                }

                ArgMin(marg, out min, out sel);
                if (min < 0.0f)
                {
                    loopN++;
                    if (loopN == StabilizeMaxLoop)
                    {
                        break;
                    }
                }
            }

            nlsf[0] = minSpacing[0] + marg[0];
            float run = nlsf[0];
            for (int i = 1; i < L; i++)
            {
                run = run + marg[i] + minSpacing[i];
                nlsf[i] = run;
            }
        }

        /// <summary>Rebuilds the quantized NLSF from the stage indices and the previous frame's NLSF.</summary>
        public static float[] SmplReconstructNLSF(SmplSynthTables t, int stage1, int config, int grid, int[] stage2, float[] prevNlsf)
        {
            float[][] val = t.Valtables[stage1][config][grid];
            var resid = new float[SmplOrder];
            for (int k = 0; k < SmplOrder; k++)
            {
                int sym = stage2[k];
                if (sym >= 0 && sym < val[k].Length)
                {
                    resid[k] = val[k][sym];
                }
            }

            var o = new float[SmplOrder];
            int prevLen = prevNlsf == null ? 0 : prevNlsf.Length;
            if (grid == 16)
            {
                var b = new float[SmplOrder];
                float[] baseTbl = t.Grid16W[1 - stage1];
                float alpha = t.Grid16Alpha[stage1];
                for (int k = 0; k < SmplOrder; k++)
                {
                    float pv = k < prevLen ? prevNlsf[k] : 0f;
                    b[k] = pv + alpha * (baseTbl[k] - pv);
                }

                var w = new float[SmplOrder];
                SmplNlsfLaroiaWeights(b, w);
                for (int i = 0; i < SmplOrder; i++)
                {
                    w[i] = (float)Math.Sqrt(w[i]);
                }

                var decorr = new float[SmplOrder];
                SmplNlsfDecorr(t.Grid16Matrices[stage1][config], resid, decorr);
                for (int k = 0; k < SmplOrder; k++)
                {
                    o[k] = b[k] + decorr[k] / w[k];
                }

                SmplStabilizeNlsf(o, t.MinSpacing[stage1]);
                return o;
            }

            float[] cent = t.Centroids[stage1][grid];
            float[][] mat = t.Matrices[stage1][grid];
            for (int r = 0; r < SmplOrder; r++)
            {
                float acc = 2.0f * cent[r];
                for (int c = 0; c < SmplOrder; c++)
                {
                    acc += mat[c][r] * resid[c];
                }

                o[r] = acc;
            }

            SmplStabilizeNlsf(o, t.MinSpacing[stage1]);
            return o;
        }

        /// <summary>NLSF to the monic LPC coefficient vector A[0..16] (a[0]=1), double-precision polynomial.</summary>
        public static float[] SmplNLSF2A(float[] nlsf, int count)
        {
            int order = count;
            int half = order / 2;
            var cosv = new double[order];
            for (int i = 0; i < order; i++)
            {
                cosv[i] = Math.Cos(nlsf[i]);
            }

            var p = new double[half + 1];
            var q = new double[half + 1];
            SmplNlsfPoly(p, cosv, half, 0);
            SmplNlsfPoly(q, cosv, half, 1);

            var a = new float[order + 1];
            a[0] = 1.0f;
            for (int k = 0; k < half; k++)
            {
                double pt = p[k + 1] + p[k];
                double qt = q[k + 1] - q[k];
                a[k + 1] = (float)(0.5 * (pt + qt));
                a[order - k] = (float)(0.5 * (pt - qt));
            }

            return a;
        }

        public static float[] SmplNLSF2A(float[] nlsf)
        {
            return SmplNLSF2A(nlsf, nlsf.Length);
        }

        private static void SmplNlsfPoly(double[] o, double[] cosv, int half, int parity)
        {
            o[0] = 1.0;
            o[1] = -2.0 * cosv[parity];
            for (int k = 1; k < half; k++)
            {
                double c = -2.0 * cosv[2 * k + parity];
                o[k + 1] = 2.0 * o[k - 1] + c * o[k];
                for (int n = k; n > 1; n--)
                {
                    o[n] += o[n - 2] + c * o[n - 1];
                }

                o[1] += c;
            }
        }

        /// <summary>out[n] = ex[n] - sum a[j]*out[n-j] in double; state holds the previous order outputs.</summary>
        internal static void SmplLpcSynthesis(float[] ex, float[] a, float[] o, float[] state)
        {
            const int order = SmplOrder;
            for (int n = 0; n < ex.Length; n++)
            {
                double acc = ex[n];
                for (int j = 1; j <= order; j++)
                {
                    double prev = n >= j ? o[n - j] : state[order + n - j];
                    acc -= (double)a[j] * prev;
                }

                o[n] = (float)acc;
            }

            if (o.Length >= order)
            {
                Array.Copy(o, o.Length - order, state, 0, order);
            }
        }

        /// <summary>Quantized log-gain to linear gain (fast pow2 bit-cast).</summary>
        public static double SmplGainLin(int gainQ)
        {
            float y = (float)gainQ * 6.103515625e-05f * 0.10000000149011612f * 27749388.0f + 1064866816.0f;
            int i;
            if (y < 2147483648.0f && y > -2147483648.0f)
            {
                i = (int)y;
            }
            else
            {
                i = int.MinValue;
            }

            float f = MlowMath.Float32FromBits(unchecked((uint)i)) - 3.1622775509276835e-09f;
            if (f < 0.0f)
            {
                f = 0.0f;
            }

            return f;
        }

        internal static float SmplFloorF32(float x)
        {
            int i = (int)x;
            if ((float)i > x)
            {
                i--;
            }

            return i;
        }

        /// <summary>Normalized LTP gain to the fractional gain.</summary>
        public static float SmplLtpFracGain(double normGain)
        {
            return (float)normGain * -0.16999998688697815f + 0.3499999940395355f;
        }

        /// <summary>8-tap symmetric FIR16 application, in place over sig (in/out overlap allowed).</summary>
        private static void SmplFir8(float[] sig, int inBase, int outBase, int cnt)
        {
            for (int jj = 0; jj < cnt; jj++)
            {
                float acc = 0;
                for (int i = 0; i < 8; i++)
                {
                    acc += (sig[inBase + jj + i] + sig[inBase + jj + 15 - i]) * Fir16[i];
                }

                sig[outBase + jj] = acc;
            }
        }

        /// <summary>Fractional LTP + interpolation (func 3523); reads sig backward from sigEnd, mutates sig.</summary>
        private static void SmplFracLtp(float[] lag, int numSubfr, float[] sig, int sigEnd, int stateLen, float[] o)
        {
            int lb = sigEnd - (40 * numSubfr - stateLen);
            for (int sf = 0; sf < numSubfr; sf++)
            {
                float fl = SmplFloorF32(lag[sf]);
                int intLag = (int)fl;
                if ((float)intLag == lag[sf])
                {
                    for (int k = 0; k < 40; k++)
                    {
                        sig[lb + k] = sig[lb + k - intLag];
                    }

                    for (int k = 0; k < 40; k++)
                    {
                        o[sf * 40 + k] = sig[lb + k];
                        o[(numSubfr + sf) * 40 + k] = sig[lb + k - intLag - 1] + sig[lb + k - intLag + 1];
                    }
                }
                else
                {
                    int b = (numSubfr + sf) * 40;
                    for (int k = 0; k < 40; k++)
                    {
                        o[b + k] = sig[lb - intLag - 1 + k] + sig[lb - intLag + 1 + k];
                    }

                    float l10 = 0;
                    for (int j = 0; j < 16; j++)
                    {
                        l10 += sig[lb - 9 - intLag + j] * Fir16[j];
                    }

                    SmplFir8(sig, lb - intLag - 8, lb, 40);
                    float l11 = 0;
                    for (int j = 0; j < 16; j++)
                    {
                        l11 += sig[lb + 32 - intLag + j] * Fir16[j];
                    }

                    for (int k = 0; k < 40; k++)
                    {
                        o[sf * 40 + k] = sig[lb + k];
                    }

                    o[b] = l10 + sig[lb + 1];
                    for (int k = 0; k < 38; k++)
                    {
                        o[b + 1 + k] = sig[lb + k] + sig[lb + 2 + k];
                    }

                    o[b + 39] = l11 + sig[lb + 38];
                }

                lb += 40;
            }
        }

        /// <summary>Per-subframe LTP gain-apply (func 3522).</summary>
        private static void SmplExcGainApply(int subLen, float[] input, SmplExcGainState st, float[] o, float gain)
        {
            if (gain != 0.0f)
            {
                float s5 = st.S1;
                float s6 = (s5 + s5) + st.S0;
                float d = st.S0 - s5;
                float absD = MlowMath.AbsF32(d);
                float absS6 = MlowMath.AbsF32(s6);
                float mn = absD + gain;
                if (absS6 < mn)
                {
                    mn = absS6;
                }

                float t = d * mn / (absD + 1e-12f);
                st.S1 = (s6 - t) / 3.0f;
                st.S0 = (2.0f * t + s6) / 3.0f;
            }

            if (subLen == 0)
            {
                return;
            }

            float s0 = st.S0;
            for (int n = 0; n < subLen; n++)
            {
                o[n] = s0 * input[n];
            }

            float s1 = st.S1;
            for (int n = 0; n < subLen; n++)
            {
                o[n] += s1 * input[subLen + n];
            }
        }

        /// <summary>Fractional LTP prediction for one 80-sample subframe (func 3523 + func 3522).</summary>
        public static void SmplLtpSubframePred(float[] hist, int histPos, float lagF, float gainFrac, SmplExcGainState gst, float[] predOut)
        {
            var fracOut = new float[2 * 2 * 40];
            var lags = new[] { lagF, lagF };
            SmplFracLtp(lags, 2, hist, histPos - 648, FracStateLen, fracOut);
            SmplExcGainApply(SmplSubfrLen, fracOut, gst, predOut, gainFrac);
        }

        /// <summary>
        /// WASM-domain internal-frame synthesis (unused on the CELP decode path). Returns the
        /// PCM signal; nlsf (the next frame's prevNLSF) is returned through the out parameter.
        /// </summary>
        public static float[] SynthInternalFrame(
            SmplSynthTables t,
            SmplFrameSynth st,
            int stage1,
            int config,
            int grid,
            int[] stage2,
            float[] prevNlsf,
            int[] pulses,
            int[] gainQ,
            SmplPitchSynth pitch,
            out float[] nlsf)
        {
            nlsf = SmplReconstructNLSF(t, stage1, config, grid, stage2, prevNlsf);
            float[] a = SmplNLSF2A(nlsf);

            var ex = new float[SmplIntfLen];
            for (int n = 0; n < SmplIntfLen; n++)
            {
                int sf = n / SmplSubfrLen;
                int gq = sf < gainQ.Length ? gainQ[sf] : 0;
                ex[n] = (float)(pulses[n] * (SmplGainLin(gq) * SmplSubfrLen));
            }

            float[] hist = st.LtpHist;
            if (pitch.Voiced)
            {
                float gainFrac = SmplLtpFracGain(pitch.NormGain);
                var predOut = new float[SmplSubfrLen];
                st.Gst = new SmplExcGainState();
                for (int sf = 0; sf < SmplSubfrCount; sf++)
                {
                    float lagF = (float)pitch.LagSubfr[sf];
                    int intLag = (int)lagF;
                    if (intLag <= 0)
                    {
                        int from = sf * SmplSubfrLen;
                        Array.Copy(ex, from, hist, SmplLtpHist + from, SmplSubfrLen);
                        continue;
                    }

                    int exBase = sf * SmplSubfrLen;
                    int histPos = SmplLtpHist + exBase;
                    if (intLag > 0 && intLag < SmplSubfrLen)
                    {
                        for (int n = intLag; n < SmplSubfrLen; n++)
                        {
                            ex[exBase + n] += GLtp * ex[exBase + n - intLag];
                        }
                    }

                    SmplLtpSubframePred(hist, histPos, lagF, gainFrac, st.Gst, predOut);
                    for (int n = 0; n < SmplSubfrLen; n++)
                    {
                        ex[exBase + n] += predOut[n];
                    }

                    Array.Copy(ex, exBase, hist, histPos, SmplSubfrLen);
                }
            }
            else
            {
                Array.Copy(ex, 0, hist, SmplLtpHist, SmplIntfLen);
            }

            var o = new float[SmplIntfLen];
            SmplLpcSynthesis(ex, a, o, st.LpcState);

            Array.Copy(hist, SmplIntfLen, hist, 0, SmplLtpHist);
            for (int i = SmplLtpHist + SmplIntfLen; i < LtpHistLen; i++)
            {
                hist[i] = 0.0f;
            }

            return o;
        }

        /// <summary>smpl_quant_nrg_res, num_subfr==4.</summary>
        public static NrgResQuant QuantNrgRes4(float[] nrgres)
        {
            var nrgresDB = new float[4];
            float frameDB = 0;
            for (int i = 0; i < 4; i++)
            {
                float v = 10.0f * (float)Math.Log10(nrgres[i] + SmplResNrgBias);
                if (v > ResNrgMaxDB)
                {
                    v = ResNrgMaxDB;
                }

                nrgresDB[i] = v;
                frameDB += v;
            }

            frameDB /= 4.0f;
            const float scQ14 = 1.0f / (1 << 14);
            int frameQi = (int)MlowMath.GoRound((frameDB - ResNrgMinDB) / (scQ14 * (float)NrgStepDBQ14x4));
            int frameDbqQ14 = frameQi * NrgStepDBQ14x4;
            frameDbqQ14 += (int)ResNrgMinDB * (1 << 14);
            for (int i = 0; i < 4; i++)
            {
                nrgresDB[i] -= (float)frameDbqQ14 * scQ14;
            }

            const float scQ10 = 1.0f / (1 << 10);
            float bestRD = 1e30f;
            int qi = 0;
            for (int n = 0; n < ResNrgShapeCBN4; n++)
            {
                float rd = 0;
                for (int i = 0; i < 4; i++)
                {
                    float d = nrgresDB[i] - (float)NrgresShapeCB4Q10[n * 4 + i] * scQ10;
                    rd += d * d;
                }

                if (rd < bestRD)
                {
                    qi = n;
                    bestRD = rd;
                }
            }

            var r = new NrgResQuant { FrameQi = frameQi, ShapeQi = qi };
            for (int i = 0; i < 4; i++)
            {
                r.DbqQ14[i] = frameDbqQ14 + NrgresShapeCB4Q10[qi * 4 + i] * 16;
            }

            return r;
        }
    }
}
