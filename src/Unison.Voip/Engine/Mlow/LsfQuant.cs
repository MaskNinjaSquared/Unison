// =============================================================================
// LsfQuant
//
// LSF quantizer codebook types (built by LsfSeed) and the two-stage RD LSF
// quantizer (smpl_lsf_quant / smpl_lsf_quant_cond). Single precision
// throughout: qi[] is decided by float32 comparisons.
// Ports: meowcaller mlow/lsf_quant.go (MIT)
// =============================================================================
namespace Unison.Voip.Engine.Mlow
{
    /// <summary>Per-codebook (voiced/unvoiced) stage-1 table set.</summary>
    internal sealed class St1Tables
    {
        public float[][] Cbhalf;
        public float[][] CInv;
        public float[] BitsCond;
        public float[][][] Rotcond;
        public float[][] CbCinv;
        public float[][][] We;
        public float[] Bits;
        public float[][][] Wie;
    }

    /// <summary>One stage-2 table set (per voiced/lowRate/qi1); rows are ragged.</summary>
    internal sealed class St2Tables
    {
        public int[] NumQlvls;
        public float[][] Qlvls;
        public float[][] NumBits;
    }

    /// <summary>LSF codebook tables: St1 [2], St2 [2][2][17], Min/MaxQi [2][2][17][16], Qstep [2][2].</summary>
    internal sealed class LsfCb
    {
        public St1Tables[] St1;
        public St2Tables[][][] St2;
        public int[][][][] MinQi;
        public int[][][][] MaxQi;
        public float[][] Qstep;
        public float[] MeanV;
        public float[] MeanUV;
        public float[] RegCond;
        public float[] MinDistV;
        public float[] MinDistUV;
    }

    /// <summary>Qi[0] = grid, Qi[1..16] = stage2, and the reconstructed quantized NLSF.</summary>
    internal sealed class LsfQuantResult
    {
        public readonly int[] Qi = new int[MlowMath.SmplLpcOrder + 1];
        public readonly float[] QLsf = new float[MlowMath.SmplLpcOrder];
    }

    internal static class LsfQuant
    {
        public const int LsfCbCentroids = 16;
        public const float LsfQstepCondMult = 0.9f;

        private const int Order = MlowMath.SmplLpcOrder;

        private sealed class CondParams
        {
            public float[] St1Cbhalf;
            public float[] St1CbCinv;
            public float[][] St1We;
            public float[][] St1Wie;
        }

        public static LsfCb LoadLsfCb()
        {
            return LsfSeed.LoadLsfBuilt().Cb;
        }

        internal static int SmplSign(float a)
        {
            if (a > 0)
            {
                return 1;
            }

            return a == 0 ? 0 : -1;
        }

        private static void SubVec(float[] y, float[] z, float[] o)
        {
            for (int i = 0; i < Order; i++)
            {
                o[i] = y[i] - z[i];
            }
        }

        private static float DotProd(float[] a, float[] b)
        {
            float s = 0;
            for (int i = 0; i < Order; i++)
            {
                s += a[i] * b[i];
            }

            return s;
        }

        private static float Werr(float[] x, float[] y, float[] w)
        {
            float s = 0;
            for (int k = 0; k < Order; k++)
            {
                float e = x[k] - y[k];
                s += w[k] * e * e;
            }

            return s;
        }

        /// <summary>y[i] = sum_j c[j][i]*x[j].</summary>
        internal static void MatrixMultTransp16(float[][] c, float[] x, float[] y, int lenX)
        {
            var yt = new float[Order];
            float xtmp = x[0];
            for (int i = 0; i < Order; i++)
            {
                yt[i] = c[0][i] * xtmp;
            }

            for (int j = 1; j < lenX; j++)
            {
                float xj = x[j];
                for (int i = 0; i < Order; i++)
                {
                    yt[i] += c[j][i] * xj;
                }
            }

            System.Array.Copy(yt, y, Order);
        }

        /// <summary>Top-K indices of the K largest values (descending), ties toward the lower index.</summary>
        internal static void GetMaxiK(float[] x, int n, int[] idx, int k)
        {
            var used = new bool[n];
            for (int slot = 0; slot < k; slot++)
            {
                int bestI = -1;
                float bestV = float.NegativeInfinity;
                for (int i = 0; i < n; i++)
                {
                    if (used[i])
                    {
                        continue;
                    }

                    if (x[i] > bestV)
                    {
                        bestV = x[i];
                        bestI = i;
                    }
                }

                if (bestI < 0)
                {
                    idx[slot] = 0;
                }
                else
                {
                    used[bestI] = true;
                    idx[slot] = bestI;
                }
            }
        }

        /// <summary>RD weight = 1/sqrt(|A(e^jw)|^2 * scale), scale = 1/min. a is monic A[0..16].</summary>
        internal static float[] LsfWeightsSpectral(float[] a, float[] lsf)
        {
            var lsfw = new float[Order];
            for (int i = 0; i < Order; i++)
            {
                float eRe = MlowMath.CosF32(lsf[i]);
                float eIm = MlowMath.SinF32(lsf[i]);
                float accRe = 1.0f;
                float accIm = 0.0f;
                float epRe = eRe;
                float epIm = eIm;
                for (int j = 1; j < Order; j++)
                {
                    accRe += epRe * a[j];
                    accIm -= epIm * a[j];
                    float nr = epRe * eRe - epIm * eIm;
                    float ni = epRe * eIm + epIm * eRe;
                    epRe = nr;
                    epIm = ni;
                }

                accRe += epRe * a[Order];
                accIm -= epIm * a[Order];
                lsfw[i] = accRe * accRe + accIm * accIm;
            }

            float minLsfw = lsfw[0];
            for (int i = 1; i < Order; i++)
            {
                if (lsfw[i] < minLsfw)
                {
                    minLsfw = lsfw[i];
                }
            }

            float scale = 1.0f / minLsfw;
            for (int i = 0; i < Order; i++)
            {
                lsfw[i] = 1.0f / MlowMath.SqrtF32(lsfw[i] * scale);
            }

            return lsfw;
        }

        /// <summary>Laroia LSF weighting (inverse adjacent-spacing sum).</summary>
        public static float[] LsfWeightsLaroia(float[] lsf)
        {
            float minDist = 1e-3f;
            var invDelta = new float[Order + 1];
            invDelta[0] = 1.0f / MlowMath.MaxF32(lsf[0], minDist);
            for (int i = 1; i < Order; i++)
            {
                invDelta[i] = 1.0f / MlowMath.MaxF32(lsf[i] - lsf[i - 1], minDist);
            }

            invDelta[Order] = 1.0f / MlowMath.MaxF32(MlowMath.SmplPi - lsf[Order - 1], minDist);
            var lsfw = new float[Order];
            for (int i = 0; i < Order; i++)
            {
                lsfw[i] = invDelta[i] + invDelta[i + 1];
            }

            return lsfw;
        }

        private static void FindMin(float[] d, int n, out float m, out int mi)
        {
            m = d[0];
            mi = 0;
            for (int i = 1; i < n + 1; i++)
            {
                if (d[i] < m)
                {
                    m = d[i];
                    mi = i;
                }
            }
        }

        /// <summary>SMPL_lsf_min_dist: push LSFs apart so consecutive spacings exceed minDist.</summary>
        internal static void LsfMinDist(float[] lsfs, float[] minDist)
        {
            const int n = Order;
            var dlsfs = new float[Order + 1];
            dlsfs[0] = lsfs[0] - minDist[0];
            for (int i = 1; i < n; i++)
            {
                dlsfs[i] = (lsfs[i] - lsfs[i - 1]) - minDist[i];
            }

            dlsfs[n] = (MlowMath.SmplPi - lsfs[n - 1]) - minDist[n];
            float dm;
            int minIx;
            FindMin(dlsfs, n, out dm, out minIx);
            if (dm > 0.0f)
            {
                return;
            }

            for (int k = 0; k < 1000; k++)
            {
                float delta = (float)k * 1.0e-6f - dm;
                dlsfs[minIx] += delta;
                if (minIx == 0)
                {
                    dlsfs[1] -= delta;
                }
                else if (minIx == n)
                {
                    dlsfs[n - 1] -= delta;
                }
                else
                {
                    delta *= 0.5f;
                    dlsfs[minIx - 1] -= delta;
                    dlsfs[minIx + 1] -= delta;
                }

                FindMin(dlsfs, n, out dm, out minIx);
                if (dm >= 0.0f)
                {
                    lsfs[0] = dlsfs[0] + minDist[0];
                    for (int i = 1; i < n; i++)
                    {
                        lsfs[i] = lsfs[i - 1] + (dlsfs[i] + minDist[i]);
                    }

                    return;
                }
            }
        }

        private static void VqTemp(float[] lsf, float[][] cbhalf, float[][] cbCinv, CondParams cond, int surv, int[] idxs)
        {
            var err = new float[LsfCbCentroids + 1];
            var tmp = new float[Order];
            for (int s = 0; s < LsfCbCentroids; s++)
            {
                SubVec(cbhalf[s], lsf, tmp);
                err[s] = -DotProd(tmp, cbCinv[s]);
            }

            int cbCentroids = LsfCbCentroids;
            if (cond != null)
            {
                SubVec(cond.St1Cbhalf, lsf, tmp);
                err[LsfCbCentroids] = -DotProd(tmp, cond.St1CbCinv);
                cbCentroids++;
            }

            GetMaxiK(err, cbCentroids, idxs, surv);
        }

        private static LsfQuantResult LsfQuantCore(LsfCb cb, float[] a, float[] nlsf, int voiced, int lowRate, CondParams cond, float rdWAdj, int surv)
        {
            St1Tables st1 = cb.St1[voiced];
            St2Tables[] st2v = cb.St2[voiced][lowRate];
            int[][] minQi = cb.MinQi[voiced][lowRate];
            int[][] maxQi = cb.MaxQi[voiced][lowRate];
            float[] minDist = voiced == 1 ? cb.MinDistV : cb.MinDistUV;

            var lsf = new float[Order];
            System.Array.Copy(nlsf, lsf, Order);
            float[] wlsf = LsfWeightsSpectral(a, lsf);

            float qstep = cb.Qstep[voiced][lowRate];
            float qstepCond = qstep * LsfQstepCondMult;

            var qim1 = new int[LsfCbCentroids + 1];
            VqTemp(lsf, st1.Cbhalf, st1.CbCinv, cond, surv, qim1);

            float rdBest = float.MaxValue;
            var result = new LsfQuantResult();

            for (int s1 = 0; s1 < surv; s1++)
            {
                int qi1 = qim1[s1];
                bool isCond = qi1 == LsfCbCentroids;

                var lsfq1 = new float[Order];
                for (int i = 0; i < Order; i++)
                {
                    lsfq1[i] = (isCond ? cond.St1Cbhalf[i] : st1.Cbhalf[qi1][i]) * 2.0f;
                }

                var qerrIn = new float[Order];
                SubVec(lsf, lsfq1, qerrIn);
                float[][] wiePtr = isCond ? cond.St1Wie : st1.Wie[qi1];
                var qerr = new float[Order];
                MatrixMultTransp16(wiePtr, qerrIn, qerr, Order);

                float invQstep = isCond ? 1.0f / qstepCond : 1.0f / qstep;
                for (int i = 0; i < Order; i++)
                {
                    qerr[i] *= invQstep;
                }

                float bits = cond == null ? st1.Bits[qi1] : st1.BitsCond[qi1];

                var alt = new int[Order];
                var absQerr = new float[Order];
                var qres = new float[Order];
                var qi2 = new int[Order];
                St2Tables st2 = st2v[qi1];
                for (int i = 0; i < Order; i++)
                {
                    int qi2i = (int)MlowMath.RoundF32(qerr[i]);
                    int mn = minQi[qi1][i];
                    int mx = maxQi[qi1][i];
                    if (qi2i > mx)
                    {
                        qi2i = mx;
                    }

                    if (qi2i < mn)
                    {
                        qi2i = mn;
                    }

                    qerr[i] -= (float)qi2i;
                    alt[i] = SmplSign(qerr[i]);
                    if ((qi2i == mx && alt[i] > 0) || (qi2i == mn && alt[i] < 0))
                    {
                        absQerr[i] = -1.0f;
                    }
                    else
                    {
                        absQerr[i] = MlowMath.AbsF32(qerr[i]);
                    }

                    qi2i -= mn;
                    bits += st2.NumBits[i][qi2i];
                    qres[i] = st2.Qlvls[i][qi2i];
                    qi2[i] = qi2i;
                }

                var iAlt = new int[Order];
                GetMaxiK(absQerr, Order, iAlt, surv);

                float[][] wePtr = isCond ? cond.St1We : st1.We[qi1];
                var lsfq = new float[Order];
                MatrixMultTransp16(wePtr, qres, lsfq, Order);
                for (int i = 0; i < Order; i++)
                {
                    lsfq[i] += lsfq1[i];
                }

                int surv2 = surv - s1;
                int indChgd = -1;
                float bitsOrig = bits;
                var lsfqBase = (float[])lsfq.Clone();
                float curBits = bits;
                for (int s2 = 0; s2 < surv2; s2++)
                {
                    LsfMinDist(lsfq, minDist);
                    float w = Werr(lsf, lsfq, wlsf);
                    float rd = 0.5f * (float)Order * MlowMath.Log2F32(w) * rdWAdj + curBits;
                    if (rd < rdBest)
                    {
                        rdBest = rd;
                        result.Qi[0] = qi1;
                        System.Array.Copy(qi2, 0, result.Qi, 1, Order);
                        System.Array.Copy(lsfq, result.QLsf, Order);
                    }

                    if (s2 == surv2 - 1 || absQerr[iAlt[s2]] < 0.25f)
                    {
                        break;
                    }

                    if (s2 > 0)
                    {
                        qi2[indChgd] -= alt[indChgd];
                    }

                    indChgd = iAlt[s2];
                    int ic = indChgd;
                    int qi2Old = qi2[ic];
                    qi2[ic] += alt[ic];
                    int qi2New = qi2[ic];
                    float qlvlsDiff = st2.Qlvls[ic][qi2New] - st2.Qlvls[ic][qi2Old];
                    for (int i = 0; i < Order; i++)
                    {
                        lsfq[i] = lsfqBase[i] + qlvlsDiff * wePtr[ic][i];
                    }

                    curBits = bitsOrig + st2.NumBits[ic][qi2New] - st2.NumBits[ic][qi2Old];
                }
            }

            return result;
        }

        /// <summary>Non-conditional LSF quantization (smpl_lsf_quant). a is the monic LPC A[0..16].</summary>
        public static LsfQuantResult Quant(float[] a, float[] nlsf, int voiced, int lowRate, float rdWAdj, int surv)
        {
            return LsfQuantCore(LoadLsfCb(), a, nlsf, voiced, lowRate, null, rdWAdj, surv);
        }

        /// <summary>Conditional LSF quantization given the previous frame's quantized NLSF (smpl_lsf_quant_cond).</summary>
        public static LsfQuantResult LsfQuantCond(float[] a, float[] nlsf, float[] lsfqPrev, int voiced, int lowRate, float rdWAdj, int surv)
        {
            LsfCb cb = LoadLsfCb();
            St1Tables st1 = cb.St1[voiced];
            float[] cbMean = voiced == 1 ? cb.MeanV : cb.MeanUV;
            float reg = cb.RegCond[voiced];
            var lsfqPrevReg = new float[Order];
            var st1Cbhalf = new float[Order];
            for (int i = 0; i < Order; i++)
            {
                lsfqPrevReg[i] = lsfqPrev[i] + reg * (cbMean[i] - lsfqPrev[i]);
                st1Cbhalf[i] = 0.5f * lsfqPrevReg[i];
            }

            var st1CbCinv = new float[Order];
            MatrixMultTransp16(st1.CInv, lsfqPrevReg, st1CbCinv, Order);
            float[][] we, wie;
            RotApplyWght(st1.Rotcond[lowRate], lsfqPrevReg, out we, out wie);
            var cond = new CondParams { St1Cbhalf = st1Cbhalf, St1CbCinv = st1CbCinv, St1We = we, St1Wie = wie };
            return LsfQuantCore(cb, a, nlsf, voiced, lowRate, cond, rdWAdj, surv);
        }

        /// <summary>smpl_rot_apply_wght: wrot1 (=we) and wrot2 (=wie) from the rotation and Laroia-weighted LSF.</summary>
        internal static void RotApplyWght(float[][] rot, float[] lsf, out float[][] we, out float[][] wie)
        {
            float[] lsfw = LsfWeightsLaroia(lsf);
            for (int i = 0; i < Order; i++)
            {
                lsfw[i] = MlowMath.SqrtF32(lsfw[i]);
            }

            var lsfwInv = new float[Order];
            for (int i = 0; i < Order; i++)
            {
                lsfwInv[i] = 1.0f / lsfw[i];
            }

            we = new float[Order][];
            wie = new float[Order][];
            for (int i = 0; i < Order; i++)
            {
                we[i] = new float[Order];
                wie[i] = new float[Order];
            }

            for (int i = 0; i < Order; i++)
            {
                for (int j = 0; j < Order; j++)
                {
                    we[i][j] = rot[i][j] * lsfwInv[j];
                    wie[j][i] = rot[i][j] * lsfw[j];
                }
            }
        }
    }
}
