// =============================================================================
// SmplLpc
//
// Encoder LPC front-end: 20 ms analysis window, FFT power spectrum -> cosine-sum
// autocorrelation -> Schur reflection coefficients -> monic LPC with bandwidth
// expansion, the fixed-point silk A->NLSF, and the per-subframe LSF
// interpolation with stabilization.
// Ports: meowcaller mlow/lpc.go (MIT)
// =============================================================================
using System;

namespace Unison.Voip.Engine.Mlow
{
    internal static class SmplLpc
    {
        public const int Order = MlowMath.SmplLpcOrder;
        public const int LpcBufLen = 448;
        public const int Nfft = 512;
        public const int FLen = Nfft / 2 + 1;

        private const double SmplPIF64 = (double)MlowMath.SmplPI;
        private const float LpcReg = 5e-7f;
        private const float LpcBwe = 0.9999f;
        private const int Win120msLen = 264;
        private const int Win3LongLen = 64;
        private const int Win3ShortLen = 32;
        private const int Nfft4 = Nfft / 4;

        private const int Subfrs = 4;
        private const float MaxRcStable = 0.9995f;

        private const int LsfCosTabSzFix = 128;
        private const int BinDivStepsA2NlsfFix = 3;
        private const int MaxIterationsA2NlsfFix = 16;
        private const int SilkInt16Max = 32767;

        private static readonly float[][] LsfInterpol4Tbl =
        {
            new[] { 0.55f, 0.88f, 1.0f, 1.0f },
            new[] { 0.3f, 0.65f, 0.95f, 1.0f },
        };

        private static readonly float[] Win1 = GenSinWin(Win120msLen);
        private static readonly float[] Win3Long = GenCosWin(Win3LongLen);
        private static readonly float[] Win3Short = GenCosWin(Win3ShortLen);
        private static readonly Lazy<DctTables> Dct = new Lazy<DctTables>(BuildDctTables);

        private sealed class DctTables
        {
            public readonly double[][] Cdif = NewRows(Order / 2);
            public readonly double[][] Csumdiff = NewRows(Order / 4);
            public readonly double[][] Csumsum = NewRows(Order / 4);

            private static double[][] NewRows(int n)
            {
                var r = new double[n][];
                for (int i = 0; i < n; i++)
                {
                    r[i] = new double[Nfft4];
                }

                return r;
            }
        }

        public static float[] GenSinWin(int n)
        {
            var w = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = ((float)i + 1.0f) / ((float)n + 1.0f) * MlowMath.SmplPI / 2.0f;
                w[i] = (float)Math.Sin(t);
            }

            return w;
        }

        public static float[] GenCosWin(int n)
        {
            var w = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = ((float)i + 1.0f) / ((float)n + 1.0f) * MlowMath.SmplPI / 2.0f;
                w[i] = (float)Math.Cos(t);
            }

            return w;
        }

        /// <summary>Applies the 20 ms LPC analysis window; useLongWin selects the 64- vs 32-tap trailing taper.</summary>
        public static float[] SmplWindowLpc20(float[] input, bool useLongWin)
        {
            var o = new float[LpcBufLen];
            SmplWindowLpc20(input, useLongWin, o);
            return o;
        }

        public static void SmplWindowLpc20(float[] input, bool useLongWin, float[] o)
        {
            float[] win3 = useLongWin ? Win3Long : Win3Short;
            int win3len = useLongWin ? Win3LongLen : Win3ShortLen;
            for (int i = 0; i < Win120msLen; i++)
            {
                o[i] = input[i] * Win1[i];
            }

            int mid = LpcBufLen - Win120msLen - Win3LongLen;
            Array.Copy(input, Win120msLen, o, Win120msLen, mid);
            int b = LpcBufLen - Win3LongLen;
            for (int i = 0; i < win3len; i++)
            {
                o[b + i] = input[b + i] * win3[i];
            }

            if (!useLongWin)
            {
                for (int s = b + Win3ShortLen; s < b + Win3LongLen; s++)
                {
                    o[s] = 0.0f;
                }
            }
        }

        private static void GenCosRow(double domega, double scale, double[] row)
        {
            double omega = 0.0;
            double twoPi = 2.0 * SmplPIF64;
            for (int k = 0; k < Nfft4; k++)
            {
                row[k] = Math.Cos(omega) * scale;
                omega = (omega + domega) % twoPi;
                if (omega < 0)
                {
                    omega += twoPi;
                }
            }
        }

        private static DctTables BuildDctTables()
        {
            double twoPi = 2.0 * SmplPIF64;
            double nfft = Nfft;
            var t = new DctTables();
            for (int j = 0; j < Order / 2; j++)
            {
                GenCosRow((double)(1 + j * 2) * twoPi / nfft, 2.0 / nfft, t.Cdif[j]);
            }

            for (int j = 0; j < Order / 4; j++)
            {
                GenCosRow((double)(2 + j * 4) * twoPi / nfft, 1.0 / nfft, t.Csumdiff[j]);
            }

            for (int j = 0; j < Order / 4; j++)
            {
                GenCosRow((double)(4 + j * 4) * twoPi / nfft, 1.0 / nfft, t.Csumsum[j]);
            }

            return t;
        }

        private static void BruteDct(DctTables t, float[] f2, double[] r)
        {
            const int half = Nfft / 2;
            double f2sum = 0.0;
            var f2dif = new double[Nfft4];
            var f2sumsum = new double[Nfft4];
            var f2sumdif = new double[Nfft4];
            for (int n = 0; n < Nfft4; n++)
            {
                double a = f2[n], bh = f2[half - n], c = f2[Nfft4 + n], d = f2[Nfft4 - n];
                f2sum += a + c;
                f2dif[n] = a - bh;
                f2sumsum[n] = a + bh + c + d;
                f2sumdif[n] = a + bh - c - d;
            }

            f2dif[0] *= 0.5;
            r[0] = (2.0 * f2sum - (double)f2[0] + (double)f2[half]) / (double)Nfft;
            for (int j = 0; j < Order / 2; j++)
            {
                r[1 + j * 2] = Dot(t.Cdif[j], f2dif);
            }

            for (int j = 0; j < Order / 4; j++)
            {
                r[2 + j * 4] = Dot(t.Csumdiff[j], f2sumdif);
            }

            for (int j = 0; j < Order / 4; j++)
            {
                r[4 + j * 4] = Dot(t.Csumsum[j], f2sumsum);
            }
        }

        private static double Dot(double[] row, double[] x)
        {
            double s = 0.0;
            for (int k = 0; k < Nfft4; k++)
            {
                s += row[k] * x[k];
            }

            return s;
        }

        /// <summary>Schur: autocorrelation to reflection coefficients, C0[0] *= regScale, rc truncated to float32.</summary>
        internal static void Ac2RcDbl(double[] corr, int order, double regScale, float[] rc)
        {
            var c0 = new double[order + 1];
            var c1 = new double[order + 1];
            Array.Copy(corr, c0, order + 1);
            c0[0] *= regScale;
            Array.Copy(c0, c1, order + 1);
            for (int i = 0; i < order; i++)
            {
                rc[i] = 0;
            }

            for (int k = 0; k < order; k++)
            {
                if (c0[k + 1] > c1[0])
                {
                    rc[k] = -1.0f;
                    break;
                }

                if (c0[k + 1] < -c1[0])
                {
                    rc[k] = 1.0f;
                    break;
                }

                if (c1[0] == 0.0)
                {
                    break;
                }

                double rcTmp = -c0[k + 1] / c1[0];
                rc[k] = (float)rcTmp;
                for (int n = 0; n < order - k; n++)
                {
                    double ctmp1 = c0[n + k + 1];
                    double ctmp2 = c1[n];
                    c0[n + k + 1] = ctmp1 + ctmp2 * rcTmp;
                    c1[n] = ctmp2 + ctmp1 * rcTmp;
                }
            }
        }

        /// <summary>Reflection coefficients to monic LPC A[0..order].</summary>
        internal static void Rc2A(float[] rc, int order, float[] a)
        {
            for (int i = 1; i < order + 1; i++)
            {
                a[i] = 0;
            }

            a[0] = 1.0f;
            for (int k = 0; k < order; k++)
            {
                float rcTmp = rc[k];
                for (int n = 0; n < (k + 1) / 2; n++)
                {
                    float tmp1 = a[n + 1];
                    float tmp2 = a[k - n];
                    a[n + 1] = tmp1 + tmp2 * rcTmp;
                    a[k - n] = tmp2 + tmp1 * rcTmp;
                }

                a[k + 1] = rcTmp;
            }
        }

        private static void BweExpand(float[] a, int order, float bwe)
        {
            float c = bwe;
            for (int i = 1; i < order + 1; i++)
            {
                a[i] *= c;
                c *= bwe;
            }
        }

        /// <summary>
        /// Full LPC analysis of a windowed buffer: a receives the post-BWE monic LPC A[0..16],
        /// f2 the power spectrum F2[0..256] the pitch and signal-mode paths consume.
        /// </summary>
        public static void SmplLpcAnalyzeWithF2(float[] windowed, float[] a, float[] f2)
        {
            var xbuf = new float[Nfft];
            Array.Copy(windowed, xbuf, LpcBufLen);
            var f = new float[Nfft];
            MlowFft.RfftForwardOrdered(xbuf, f);

            f2[0] = f[0] * f[0];
            f2[Nfft / 2] = f[1] * f[1];
            for (int i = 1; i < Nfft / 2; i++)
            {
                f2[i] = f[2 * i] * f[2 * i] + f[2 * i + 1] * f[2 * i + 1];
            }

            var r = new double[Order + 1];
            BruteDct(Dct.Value, f2, r);
            var rc = new float[Order];
            Ac2RcDbl(r, Order, (double)(1.0f + LpcReg), rc);
            Rc2A(rc, Order, a);
            BweExpand(a, Order, LpcBwe);
        }

        private static bool LpcIsStable(float[] a, double[] a0, double[] a1)
        {
            const int order = Order;
            if (a[order] * a[order] > MaxRcStable)
            {
                return false;
            }

            for (int i = 0; i < order; i++)
            {
                a0[i] = a[i + 1];
            }

            int m = order - 1;
            while (true)
            {
                double den = 1.0 - a0[m] * a0[m];
                if (den == 0.0)
                {
                    return false;
                }

                double inv = 1.0 / den;
                for (int k = 0; k < m; k++)
                {
                    a1[k] = (a0[k] - a0[m] * a0[m - k - 1]) * inv;
                }

                if (a1[m - 1] * a1[m - 1] > (double)MaxRcStable)
                {
                    return false;
                }

                if (m == 1)
                {
                    return true;
                }

                m--;
                den = 1.0 - a1[m] * a1[m];
                if (den == 0.0)
                {
                    return false;
                }

                inv = 1.0 / den;
                for (int k = 0; k < m; k++)
                {
                    a0[k] = (a1[k] - a1[m] * a1[m - k - 1]) * inv;
                }

                if (a0[m - 1] * a0[m - 1] > (double)MaxRcStable)
                {
                    return false;
                }

                if (m == 1)
                {
                    return true;
                }

                m--;
            }
        }

        private static void LpcStabilize(float[] a)
        {
            var a0 = new double[Order];
            var a1 = new double[Order];
            if (LpcIsStable(a, a0, a1))
            {
                return;
            }

            int iter = 0;
            while (true)
            {
                iter++;
                BweExpand(a, Order, 1.0f - (float)iter * 0.001f);
                if (LpcIsStable(a, a0, a1))
                {
                    return;
                }
            }
        }

        /// <summary>Per-subframe interpolated, stabilized LPC predictors [4][17] for an interpolation-weight row.</summary>
        public static float[][] SmplLpcInterpolIdx(float[] lsf, float[] prevLsf, int interpolIdx)
        {
            float[] interp = LsfInterpol4Tbl[Math.Min(interpolIdx, 1)];
            var prev = new float[Order];
            if (prevLsf != null && prevLsf.Length == Order && prevLsf[Order - 1] != 0.0f)
            {
                Array.Copy(prevLsf, prev, Order);
            }
            else
            {
                Array.Copy(lsf, prev, Order);
            }

            var ilsf = new float[Order];
            var predcoefs = new float[Subfrs][];
            for (int j = 0; j < Subfrs; j++)
            {
                float w = interp[j];
                if (w == 1.0f)
                {
                    Array.Copy(lsf, ilsf, Order);
                }
                else
                {
                    for (int k = 0; k < Order; k++)
                    {
                        ilsf[k] = (1.0f - w) * prev[k] + w * lsf[k];
                    }
                }

                float[] a = SmplSynth.SmplNLSF2A(ilsf);
                var pc = new float[Order + 1];
                Array.Copy(a, pc, Math.Min(Order + 1, a.Length));
                pc[0] = 1.0f;
                LpcStabilize(pc);
                predcoefs[j] = pc;
            }

            return predcoefs;
        }

        private static int SilkRshiftRound(int a, int shift)
        {
            if (shift == 1)
            {
                return (a >> 1) + (a & 1);
            }

            return ((a >> (shift - 1)) + 1) >> 1;
        }

        private static int SilkSmlaww(int a32, int b32, int c32)
        {
            return unchecked((int)((long)a32 + (((long)b32 * c32) >> 16)));
        }

        private static void SilkBwexpander32(int[] ar, int d, int chirpQ16)
        {
            unchecked
            {
                int chirp = chirpQ16;
                int chirpMinusOne = chirpQ16 - 65536;
                for (int i = 0; i < d - 1; i++)
                {
                    ar[i] = (int)(((long)chirp * ar[i]) >> 16);
                    int mul = chirp * chirpMinusOne;
                    chirp += SilkRshiftRound(mul, 16);
                }

                ar[d - 1] = (int)(((long)chirp * ar[d - 1]) >> 16);
            }
        }

        private static void SilkA2NlsfTransPoly(int[] p, int dd)
        {
            unchecked
            {
                for (int k = 2; k <= dd; k++)
                {
                    for (int n = dd; n >= k + 1; n--)
                    {
                        p[n - 2] -= p[n];
                    }

                    p[k - 2] -= p[k] << 1;
                }
            }
        }

        private static int SilkA2NlsfEvalPoly(int[] p, int x, int dd)
        {
            int xQ16 = x << 4;
            int y32 = p[dd];
            for (int n = dd - 1; n >= 0; n--)
            {
                y32 = SilkSmlaww(p[n], y32, xQ16);
            }

            return y32;
        }

        private static void SilkA2NlsfInit(int[] aQ16, int[] p, int[] q, int dd)
        {
            unchecked
            {
                p[dd] = 1 << 16;
                q[dd] = 1 << 16;
                for (int k = 0; k < dd; k++)
                {
                    p[k] = -aQ16[dd - k - 1] - aQ16[dd + k];
                    q[k] = -aQ16[dd - k - 1] + aQ16[dd + k];
                }

                for (int k = dd; k >= 1; k--)
                {
                    p[k - 1] -= p[k];
                    q[k - 1] += q[k];
                }
            }

            SilkA2NlsfTransPoly(p, dd);
            SilkA2NlsfTransPoly(q, dd);
        }

        /// <summary>Monic whitening coefficients (Q16) to NLSF (Q15); mutates aQ16 on non-convergence.</summary>
        private static void SilkA2Nlsf(int[] nlsf, int[] aQ16, int d)
        {
            int[] cosTab = SmplMem.SilkLsfCosTabFixQ12;
            int dd = d >> 1;
            var p = new int[dd + 1];
            var q = new int[dd + 1];
            SilkA2NlsfInit(aQ16, p, q, dd);

            bool useQ = false;
            int xlo = cosTab[0];
            int ylo = SilkA2NlsfEvalPoly(p, xlo, dd);

            int rootIx = 0;
            if (ylo < 0)
            {
                nlsf[0] = 0;
                useQ = true;
                ylo = SilkA2NlsfEvalPoly(q, xlo, dd);
                rootIx = 1;
            }

            int k = 1;
            int iter = 0, thr = 0;
            while (true)
            {
                int xhi = cosTab[k];
                int yhi = SilkA2NlsfEvalPoly(useQ ? q : p, xhi, dd);

                if ((ylo <= 0 && yhi >= thr) || (ylo >= 0 && yhi <= -thr))
                {
                    thr = yhi == 0 ? 1 : 0;
                    int xloL = xlo, yloL = ylo, xhiL = xhi;
                    int ffrac = -256;
                    for (int m = 0; m < BinDivStepsA2NlsfFix; m++)
                    {
                        int xmid = SilkRshiftRound(xloL + xhiL, 1);
                        int ymid = SilkA2NlsfEvalPoly(useQ ? q : p, xmid, dd);
                        if ((yloL <= 0 && ymid >= 0) || (yloL >= 0 && ymid <= 0))
                        {
                            xhiL = xmid;
                            yhi = ymid;
                        }
                        else
                        {
                            xloL = xmid;
                            yloL = ymid;
                            ffrac += 128 >> m;
                        }
                    }

                    int absYloL = yloL < 0 ? -yloL : yloL;
                    if (absYloL < 65536)
                    {
                        int den = yloL - yhi;
                        int nom = (yloL << (8 - BinDivStepsA2NlsfFix)) + (den >> 1);
                        if (den != 0)
                        {
                            ffrac += nom / den;
                        }
                    }
                    else
                    {
                        ffrac += yloL / ((yloL - yhi) >> (8 - BinDivStepsA2NlsfFix));
                    }

                    nlsf[rootIx] = Math.Min((k << 8) + ffrac, SilkInt16Max);

                    rootIx++;
                    if (rootIx >= d)
                    {
                        break;
                    }

                    useQ = (rootIx & 1) != 0;
                    xlo = cosTab[k - 1];
                    ylo = (1 - (rootIx & 2)) << 12;
                }
                else
                {
                    k++;
                    xlo = xhi;
                    ylo = yhi;
                    thr = 0;
                    if (k > LsfCosTabSzFix)
                    {
                        iter++;
                        if (iter > MaxIterationsA2NlsfFix)
                        {
                            nlsf[0] = (1 << 15) / (d + 1);
                            for (int kk = 1; kk < d; kk++)
                            {
                                nlsf[kk] = nlsf[kk - 1] + nlsf[0];
                            }

                            return;
                        }

                        SilkBwexpander32(aQ16, d, 65536 - (1 << iter));
                        SilkA2NlsfInit(aQ16, p, q, dd);
                        useQ = false;
                        xlo = cosTab[0];
                        ylo = SilkA2NlsfEvalPoly(p, xlo, dd);
                        if (ylo < 0)
                        {
                            nlsf[0] = 0;
                            useQ = true;
                            ylo = SilkA2NlsfEvalPoly(q, xlo, dd);
                            rootIx = 1;
                        }
                        else
                        {
                            rootIx = 0;
                        }

                        k = 1;
                    }
                }
            }
        }

        /// <summary>Post-BWE float LPC A[0..16] (A[0]=1) to the analysis NLSF in radians via the fixed-point silk A->NLSF.</summary>
        public static float[] SmplA2Nlsf16(float[] a)
        {
            var aQ16 = new int[Order];
            for (int i = 0; i < Order; i++)
            {
                aQ16[i] = (int)MlowMath.GoRound(-a[i + 1] * 65536.0f);
            }

            var lsfQ15 = new int[Order];
            SilkA2Nlsf(lsfQ15, aQ16, Order);
            var nlsf = new float[Order];
            for (int i = 0; i < Order; i++)
            {
                nlsf[i] = (float)lsfQ15[i] / 32768.0f * MlowMath.SmplPI;
            }

            return nlsf;
        }
    }
}
