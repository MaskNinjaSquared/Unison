// =============================================================================
// SmplNoise
//
// CELP decoder-side noise generator: the shaped residual noise the CELP
// synthesis mixes into the excitation (smpl_gennoise), plus the normalized
// bitrate and residual-energy helpers it shares with the decoder.
// Ports: meowcaller mlow/noise.go (MIT)
// =============================================================================
using System;

namespace Unison.Voip.Engine.Mlow
{
    /// <summary>Persistent decoder-side noise generator state.</summary>
    internal sealed class NoiseGenerator
    {
        public float EnvSmth;
        public float EnvLast;
        public float[] OutStateUV = new float[2];
        public float[] OutStateV = new float[2];
        public float[] CorrSmth = new float[SmplNoise.CorrOrder + 1];
        public float[] ShapeState = new float[SmplNoise.CorrOrder];
        public bool PrevVoiced;
        public int SinceUnvoiced;
        public int RandSeed;
    }

    internal static class SmplNoise
    {
        public const int MaxSfLen = 160;
        public const int CorrOrder = 2;
        public const int DctOrder = 16;
        private const int CelpFsKHz = 16;
        private const float PiNoise = 3.1415926535897f;

        private const float VNoiseGain = 0.35f;
        private const float UVNoiseGain = 0.8f;
        private const float UVFcornerHz = 800.0f;
        private const float EnvSmthCoefV = 0.95f;
        private const float EnvSmthCoefUV = 0.995f;
        private const float EnvSmthCoefUVV = 0.99f;

        private static readonly float[] CoefMAV = { 0.25f, -0.496f, 0.25f };

        private static readonly Lazy<float[][]> DctMat = new Lazy<float[][]>(BuildDct);

        /// <summary>smpl_RAND: LCG with wrapping i32 arithmetic.</summary>
        internal static int SmplRand(int seed)
        {
            return unchecked(907633515 + (int)((uint)seed * 196314165u));
        }

        /// <summary>smpl_sigmoid with the same +/-80 clamp as C.</summary>
        internal static float SmplSigmoid(float x)
        {
            if (x > 80.0f)
            {
                return 1.0f;
            }

            if (x < -80.0f)
            {
                return 0.0f;
            }

            return 1.0f / (1.0f + (float)Math.Exp(-x));
        }

        private static float SmplNrg(float[] x, int off, int n)
        {
            float nrg = 0;
            for (int i = 0; i < n; i++)
            {
                float v = x[off + i];
                nrg += v * v;
            }

            return nrg;
        }

        private static float SmplSum(float[] x, int n)
        {
            float s = 0;
            for (int i = 0; i < n; i++)
            {
                s += x[i];
            }

            return s;
        }

        private static float SmplMaximum(float[] x, int n)
        {
            float m = x[0];
            for (int i = 1; i < n; i++)
            {
                if (x[i] > m)
                {
                    m = x[i];
                }
            }

            return m;
        }

        /// <summary>smpl_gen_rand_pulses: 4-at-a-time bit-rotated white pulses scaled by 8.1e-10.</summary>
        internal static void SmplGenRandPulses(float[] noise, int l, ref int seed)
        {
            const float sc = 8.1e-10f;
            int i = 0;
            while (i + 3 < l)
            {
                seed = SmplRand(seed);
                uint s = unchecked((uint)seed);
                noise[i] = sc * (float)seed;
                noise[i + 1] = sc * (float)unchecked((int)(s << 8));
                noise[i + 2] = sc * (float)unchecked((int)(s << 16));
                noise[i + 3] = sc * (float)unchecked((int)(s << 24));
                i += 4;
            }

            while (i < l)
            {
                seed = SmplRand(seed);
                noise[i] = sc * (float)seed;
                i++;
            }
        }

        /// <summary>smpl_get_env: squared-signal smoothing envelope (4-wide, mirrors the C order).</summary>
        private static void SmplGetEnv(float[] exc, int off, int length, float smthCoef, ref float smthState, float[] env)
        {
            smthCoef *= smthCoef;
            float state = smthState + 1e-8f;
            state *= state;
            float gainCoef = 1.0f - smthCoef;
            float smthCoef2 = smthCoef * smthCoef;
            float gainSmthCoef = gainCoef * smthCoef;
            int i = 0;
            while (i + 3 < length)
            {
                float e0 = exc[off + i], e1 = exc[off + i + 1], e2 = exc[off + i + 2], e3 = exc[off + i + 3];
                float tmp0 = (e0 * e0) + (e1 * e1);
                float tmp1 = (e2 * e2) + (e3 * e3);
                float y1 = (gainCoef * tmp1) + (gainSmthCoef * tmp0) + (smthCoef2 * state);
                float y0 = (gainCoef * tmp0) + (smthCoef * state);
                env[i] = (float)Math.Sqrt(y0);
                env[i + 1] = env[i];
                env[i + 2] = (float)Math.Sqrt(y1);
                env[i + 3] = env[i + 2];
                state = y1;
                i += 4;
            }

            smthState = env[length - 1];
        }

        /// <summary>smpl_get_env0: decaying envelope when there is no excitation to seed from.</summary>
        private static void SmplGetEnv0(int length, float smthCoef, ref float smthState, float[] env)
        {
            float smthCoef2 = smthCoef * smthCoef;
            env[0] = (smthState + 1e-8f) * smthCoef;
            env[1] = env[0];
            int i = 2;
            while (i + 2 < length)
            {
                env[i + 2] = env[i - 1] * smthCoef2;
                env[i + 3] = env[i + 2];
                env[i] = env[i - 1] * smthCoef;
                env[i + 1] = env[i];
                i += 4;
            }

            env[length - 2] = env[length - 3] * smthCoef;
            env[length - 1] = env[length - 2];
            smthState = env[length - 1];
        }

        /// <summary>smpl_filt_ma1 (coef_len=2, state_len=1); x != y.</summary>
        private static void SmplFiltMA1(float[] x, int n, float c0, float c1, ref float state, float[] y)
        {
            if (c0 == 1.0f)
            {
                for (int k = 1; k < n; k++)
                {
                    y[k] = x[k] + c1 * x[k - 1];
                }
            }
            else
            {
                for (int k = 0; k < n; k++)
                {
                    y[k] = c0 * x[k];
                }

                for (int k = 1; k < n; k++)
                {
                    y[k] += c1 * x[k - 1];
                }
            }

            y[0] = c0 * x[0] + c1 * state;
            state = x[n - 1];
        }

        /// <summary>smpl_filt_ar1 (coef_len=2, state_len=1, coef[0]==1).</summary>
        private static void SmplFiltAR1(float[] x, int n, float c1, ref float state, float[] y)
        {
            float ar1 = -c1;
            float ytmp = state;
            for (int nn = 0; nn < n; nn++)
            {
                ytmp = x[nn] + ytmp * ar1;
                y[nn] = ytmp;
            }

            state = ytmp;
        }

        /// <summary>smpl_filt_arma1: MA1 then AR1, state {ma, ar}.</summary>
        private static void SmplFiltARMA1(float[] x, int n, float ma0, float ma1, float arCoef1, float[] state, float[] y)
        {
            var tmp = new float[MaxSfLen];
            float maState = state[0];
            SmplFiltMA1(x, n, ma0, ma1, ref maState, tmp);
            state[0] = maState;
            float arState = state[1];
            SmplFiltAR1(tmp, n, arCoef1, ref arState, y);
            state[1] = arState;
        }

        /// <summary>smpl_filt_ma2 (coef_len=3, state_len=2); x != y.</summary>
        internal static void SmplFiltMA2(float[] x, int n, float[] coef, float[] state, float[] y)
        {
            if (coef[0] == 1.0f)
            {
                for (int i = 1; i < n; i++)
                {
                    y[i] = x[i] + coef[1] * x[i - 1];
                }
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    y[i] = coef[0] * x[i];
                }

                for (int i = 1; i < n; i++)
                {
                    y[i] += coef[1] * x[i - 1];
                }
            }

            for (int i = 2; i < n; i++)
            {
                y[i] += coef[2] * x[i - 2];
            }

            y[0] = coef[0] * x[0] + coef[1] * state[0] + coef[2] * state[1];
            y[1] += coef[2] * state[0];
            state[0] = x[n - 1];
            state[1] = x[n - 2];
        }

        /// <summary>smpl_spec_fact2: spectral factorization of a 3-tap autocorrelation into a 3-tap MA.</summary>
        private static void SmplSpecFact2(float[] cIn, float[] a)
        {
            float c0 = cIn[0] + 1e-30f;
            float c1 = cIn[1];
            float c2 = cIn[2];
            float invC0 = 1.0f / c0;
            float r2 = c2 * invC0;
            float r1 = c1 / (c0 * (1.0f + r2));
            for (int iter = 0; iter < 2; iter++)
            {
                float v0 = 1.0f + r1 * r1 + r2 * r2;
                float v1 = r1 + r1 * r2;
                float s = -2.0f / v0;
                float da0 = s * r1;
                float da1 = s * r2;
                s = v0 * invC0;
                float e1 = s * c1 - v1;
                float e2 = s * c2 - r2;
                float r0 = 2.0f * r1 + v0 * da0;
                float r3 = 2.0f * r2 + v0 * da1;
                float rr00 = r0 * r0;
                float rr01 = r0 * r3;
                float rr11 = r3 * r3;
                float rcap1 = 1.0f + r2 + v1 * da0;
                float r4 = r1 + v1 * da1;
                rr00 += rcap1 * rcap1;
                rr01 += rcap1 * r4;
                rr11 += r4 * r4;
                float re0 = rcap1 * e1;
                float re1 = r4 * e1;
                float r2c = r2 * da0;
                float r5 = 1.0f + r2 * da1;
                rr00 += r2c * r2c;
                rr01 += r2c * r5;
                rr11 += r5 * r5;
                re0 += r2c * e2;
                re1 += r5 * e2;
                s = rr00 * rr11 - rr01 * rr01;
                if (s < 1e-4f)
                {
                    break;
                }

                s = 1.0f / s;
                r1 += (rr11 * re0 - rr01 * re1) * s;
                r2 += (-rr01 * re0 + rr00 * re1) * s;
            }

            float sc = (float)Math.Sqrt(c0 / (1.0f + r1 * r1 + r2 * r2));
            a[0] = sc;
            a[1] = sc * r1;
            a[2] = sc * r2;
        }

        /// <summary>Noise DCT matrix dct_mat_t[CORR+1][DCT_ORDER].</summary>
        private static float[][] BuildDct()
        {
            var m = new float[CorrOrder + 1][];
            for (int j = 0; j < CorrOrder + 1; j++)
            {
                m[j] = new float[DctOrder];
            }

            float sc = 1.0f / (float)Math.Sqrt(DctOrder);
            for (int i = 0; i < DctOrder; i++)
            {
                float dOmega = ((0.5f + (float)i) * PiNoise) / (float)DctOrder;
                float omega = 0;
                for (int j = 0; j < CorrOrder + 1; j++)
                {
                    m[j][i] = (float)Math.Cos(omega) * sc;
                    omega += dOmega;
                }
            }

            return m;
        }

        private static void NoiseMatMultTransp16(float[][] c, float[] x, float[] y, int lenX)
        {
            var yt = new float[DctOrder];
            float xtmp = x[0];
            for (int i = 0; i < DctOrder; i++)
            {
                yt[i] = c[0][i] * xtmp;
            }

            for (int j = 1; j < lenX; j++)
            {
                float xt = x[j];
                for (int i = 0; i < DctOrder; i++)
                {
                    yt[i] += c[j][i] * xt;
                }
            }

            Array.Copy(yt, y, DctOrder);
        }

        private static void NoiseMatMult(float[][] c, float[] x, float[] y)
        {
            for (int i = 0; i < CorrOrder + 1; i++)
            {
                float acc = 0;
                for (int k = 0; k < DctOrder; k++)
                {
                    acc += c[i][k] * x[k];
                }

                y[i] = acc;
            }
        }

        /// <summary>Maps the per-frame pulse count to the normalized bitrate.</summary>
        public static float SmplGetNormalizedBitrate(int numPulses, int frameLength16)
        {
            float pulsesPer20ms = (float)(numPulses * frameLength16) / (20.0f * 16.0f);
            return SmplSigmoid(1.4f * (float)MlowMath.Log2(pulsesPer20ms + 1.0f) - 6.5f);
        }

        /// <summary>Maps the quantized residual-energy floor to a linear residual energy.</summary>
        public static float SmplDecodeResnrg(int nrgresFrameDbqQ14, int fcbSubfrlen)
        {
            float exp = 0.1f * ((float)nrgresFrameDbqQ14 / (float)(1 << 14));
            float resnrg = (float)Math.Pow(10, exp) - SmplSynth.SmplResNrgBias;
            if (resnrg < 0.0f)
            {
                resnrg = 0.0f;
            }

            return resnrg * (float)fcbSubfrlen;
        }

        /// <summary>add_noise_uv: HP-shape the unvoiced noise and add it into noise.</summary>
        private static void AddNoiseUV(NoiseGenerator ng, float[] excNoiseUV, int l, float[] lsf, float nrgRatio, float[] noise)
        {
            float lsfHz = 16000.0f * (lsf[0] + lsf[1]) / (4.0f * PiNoise);
            float minUVFcornerHz = lsfHz * 3.0f * SmplSigmoid(0.2f / (lsf[1] - lsf[0] + 1e-30f) - 3.0f);
            float uvFcornerHz = UVFcornerHz * MlowMath.MinF32(0.6f + 0.4f * nrgRatio, 1.0f);
            uvFcornerHz = MlowMath.MaxF32(uvFcornerHz, minUVFcornerHz);
            uvFcornerHz = MlowMath.MinF32(uvFcornerHz, 1500.0f);
            float coefTmp = 6.0f * uvFcornerHz / 16000.0f;
            float g = (1.0f - 0.5f * coefTmp) * UVNoiseGain;
            var filtered = new float[MaxSfLen];
            SmplFiltARMA1(excNoiseUV, l, g, -g, -1.0f + coefTmp, ng.OutStateUV, filtered);
            Array.Copy(filtered, excNoiseUV, l);
            for (int i = 0; i < l; i++)
            {
                noise[i] += excNoiseUV[i];
            }
        }

        /// <summary>Shaped residual noise for one subframe; reads excLpc[excOff..excOff+l), writes l samples into noise.</summary>
        public static void SmplCelpGenNoise(
            NoiseGenerator ng,
            float[] excLpc,
            int excOff,
            int l,
            bool voiced,
            int numPulses,
            float nrgres,
            int fcbgIdx,
            float[] lsf,
            float normalizedBitrate,
            float[] fcbgainsUV,
            float[] noise)
        {
            float nrgRatio = 1.0f;
            var noiseUV = new float[MaxSfLen];
            var noiseV = new float[MaxSfLen];
            var noiseV2 = new float[MaxSfLen];
            var env = new float[MaxSfLen];

            if (voiced)
            {
                var corrs = new float[CorrOrder + 1];
                var c = new float[CorrOrder + 1];
                var ctgt = new float[CorrOrder + 1];
                for (int i = 0; i < CorrOrder + 1; i++)
                {
                    float acc = 0;
                    for (int k = 0; k < l - i; k++)
                    {
                        acc += excLpc[excOff + k] * excLpc[excOff + k + i];
                    }

                    corrs[i] = acc;
                }

                corrs[0] += 1e-12f;
                float corrSmthCoef = l == CelpFsKHz * 10 ? 0.4f : 0.16f;
                for (int i = 0; i < CorrOrder + 1; i++)
                {
                    ng.CorrSmth[i] += corrSmthCoef * (corrs[i] - ng.CorrSmth[i]);
                }

                float scale = VNoiseGain * VNoiseGain * corrs[0] / ng.CorrSmth[0];
                for (int i = 0; i < CorrOrder + 1; i++)
                {
                    c[i] = ng.CorrSmth[i] * scale;
                }

                c[1] *= 2.0f;
                c[2] *= 2.0f;

                float[][] dct = DctMat.Value;
                var f2 = new float[DctOrder];
                var f2Tgt = new float[DctOrder];
                NoiseMatMultTransp16(dct, c, f2, CorrOrder + 1);
                float m = SmplMaximum(f2, DctOrder) * 1.5f;
                for (int i = 0; i < DctOrder; i++)
                {
                    f2Tgt[i] = m - f2[i];
                }

                NoiseMatMult(dct, f2Tgt, ctgt);
                SmplGenRandPulses(noiseV, l, ref ng.RandSeed);
                if (!ng.PrevVoiced)
                {
                    ng.EnvSmth = ng.EnvLast;
                }

                SmplGetEnv(excLpc, excOff, l, EnvSmthCoefV, ref ng.EnvSmth, env);
                for (int i = 0; i < l; i++)
                {
                    noiseV[i] *= env[i];
                }

                float nrgNoise = SmplNrg(noiseV, 0, l);
                float inv = 1.0f / (nrgNoise + 1e-12f);
                for (int i = 0; i < CorrOrder + 1; i++)
                {
                    ctgt[i] *= inv;
                }

                var coefMA = new float[CorrOrder + 1];
                SmplSpecFact2(ctgt, coefMA);
                SmplFiltMA2(noiseV, l, coefMA, ng.ShapeState, noiseV2);

                if (!ng.PrevVoiced)
                {
                    SmplGenRandPulses(noiseUV, l, ref ng.RandSeed);
                    float envVal = ng.EnvLast * EnvSmthCoefUVV;
                    for (int i = 0; i < l; i += 2)
                    {
                        noiseUV[i] *= envVal;
                        noiseUV[i + 1] *= envVal * EnvSmthCoefUVV;
                        envVal *= EnvSmthCoefUVV * EnvSmthCoefUVV;
                    }
                }
                else if (ng.SinceUnvoiced < 2)
                {
                    for (int i = 0; i < l; i++)
                    {
                        noiseUV[i] = 0.0f;
                    }
                }

                ng.EnvLast = env[l - 1];
            }
            else
            {
                Array.Clear(ng.CorrSmth, 0, ng.CorrSmth.Length);
                Array.Clear(ng.ShapeState, 0, ng.ShapeState.Length);
                for (int i = 0; i < l; i++)
                {
                    noiseV2[i] = 0.0f;
                }

                float nrgTgt;
                if (numPulses > 0)
                {
                    nrgRatio = SmplNrg(excLpc, excOff, l) / (nrgres + 1e-20f);
                    float hardness = 10.0f + 20.0f * normalizedBitrate;
                    nrgTgt = nrgres * (float)Math.Log((float)Math.Exp(hardness * (1.0f - nrgRatio)) + 1.0f) / hardness;
                    SmplGetEnv(excLpc, excOff, l, EnvSmthCoefUV, ref ng.EnvSmth, env);
                }
                else
                {
                    nrgRatio = 0.0f;
                    nrgTgt = nrgres;
                    SmplGetEnv0(l, EnvSmthCoefUV, ref ng.EnvSmth, env);
                }

                float scale = 1.0f / (float)l;
                nrgTgt = nrgTgt * scale + 1e-30f;
                float nrgEnv = SmplNrg(env, 0, l) * scale;
                float f = (float)Math.Sqrt(nrgTgt);
                float gg = (float)Math.Sqrt(nrgTgt / nrgEnv);
                float ge = gg * env[0];
                float envLast = ng.EnvLast;
                if (envLast < MlowMath.MinF32(f, ge))
                {
                    if (f < ge)
                    {
                        gg = 0.0f;
                    }
                    else
                    {
                        f = 0.0f;
                    }
                }
                else if (envLast > MlowMath.MaxF32(f, ge))
                {
                    if (f > ge)
                    {
                        gg = 0.0f;
                    }
                    else
                    {
                        f = 0.0f;
                    }
                }
                else
                {
                    float sumEnv = SmplSum(env, l) * scale;
                    float a = nrgEnv + env[0] * env[0] - 2.0f * sumEnv * env[0];
                    float b = 2.0f * envLast * (sumEnv - env[0]);
                    float cc = envLast * envLast - nrgTgt;
                    float tmp = b * b - 4.0f * a * cc;
                    if (tmp < 1e-35f || a < 1e-25f)
                    {
                        f = 0.0f;
                        gg = 0.0f;
                    }
                    else
                    {
                        tmp = (float)Math.Sqrt(tmp);
                        scale = 0.5f / a;
                        gg = (-b + tmp) * scale;
                        f = envLast - env[0] * gg;
                        if (f < 0.0f)
                        {
                            gg = (-b - tmp) * scale;
                            f = envLast - env[0] * gg;
                        }
                    }
                }

                SmplGenRandPulses(noiseUV, l, ref ng.RandSeed);
                if (numPulses > 0)
                {
                    float maxVal = fcbgainsUV[fcbgIdx] * 0.5f;
                    for (int i = 0; i < l; i++)
                    {
                        if (excLpc[excOff + i] == 0.0f)
                        {
                            noiseUV[i] *= MlowMath.MinF32(f + gg * env[i], maxVal);
                        }
                        else
                        {
                            noiseUV[i] = 0.0f;
                        }
                    }

                    ng.EnvLast = MlowMath.MinF32(f + gg * env[l - 1], maxVal);
                }
                else
                {
                    for (int i = 0; i < l; i++)
                    {
                        noiseUV[i] *= f + gg * env[i];
                    }

                    ng.EnvLast = f + gg * env[l - 1];
                }
            }

            if (ng.PrevVoiced || voiced)
            {
                SmplFiltMA2(noiseV2, l, CoefMAV, ng.OutStateV, noise);
            }
            else
            {
                for (int i = 0; i < l; i++)
                {
                    noise[i] = 0.0f;
                }
            }

            if (ng.SinceUnvoiced < 2 || !voiced)
            {
                AddNoiseUV(ng, noiseUV, l, lsf, nrgRatio, noise);
            }
            else
            {
                ng.OutStateUV[0] = 0.0f;
                ng.OutStateUV[1] = 0.0f;
            }

            ng.PrevVoiced = voiced;
            if (voiced)
            {
                ng.SinceUnvoiced++;
            }
            else
            {
                ng.SinceUnvoiced = 0;
            }
        }
    }
}
