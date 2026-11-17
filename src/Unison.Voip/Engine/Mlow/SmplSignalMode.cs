// =============================================================================
// SmplSignalMode
//
// Voiced/unvoiced classifier: folds pitch correlation, VAD, spectral tilt,
// spectral harmonicity and short-lag strengths plus a per-stream hysteresis
// into one voicing strength (positive = voiced).
// Ports: meowcaller mlow/encoder.go (MIT)
// =============================================================================
using System;

namespace Unison.Voip.Engine.Mlow
{
    /// <summary>Per-stream voicing hysteresis + spectral-tilt background tracker (VUV_Mode).</summary>
    internal sealed class VuvMode
    {
        public float NrgLoBgn;
        public float NrgHiBgn;
        public float VoicingPrev;
        public float LastLagPrev;
    }

    internal static class SmplSignalMode
    {
        private const float VuvBias = -0.1038f;
        private const float VuvHyst = 0.05f;
        private const int TransitionIx = SmplLpc.FLen / 3;
        internal const float HarmonicityUndef = -10000.0f;
        private const int NumHarms = 4;
        internal const int HarmCacheLen = 50;

        private static readonly float[] VuvWeights = { 1.0f, 0.5f, 0.5f, 0.7f, 0.3f };
        private static readonly float[] MagPeakW = { 1.0f, 10.0f, 1.0f };
        private static readonly float[] MagValleyW = { 5.0f, 2.0f, 5.0f };

        private static float InvSigmoid(float x)
        {
            return -(float)Math.Log(1.0f / x - 1.0f);
        }

        private static float VuvDot(float[] a, int aOff, float[] b, int l)
        {
            float s = 0;
            for (int i = 0; i < l; i++)
            {
                s += a[aOff + i] * b[i];
            }

            return s;
        }

        private static float VuvSum(float[] x, int l)
        {
            float s = 0;
            for (int i = 0; i < l && i < x.Length; i++)
            {
                s += x[i];
            }

            return s;
        }

        /// <summary>Harmonic peak/valley energy ratio at avgLag, memoized per harmonic bin in cache.</summary>
        internal static float SpectralHarmonicity(float avgLag, float[] f2w, float[] cache, bool reset)
        {
            if (reset)
            {
                for (int i = 0; i < cache.Length; i++)
                {
                    cache[i] = HarmonicityUndef;
                }
            }

            float invF2StepHz = 2.0f * (float)(SmplLpc.FLen - 1) / 16000.0f;
            float harmHz = 16000.0f / avgLag;
            int harmIx = (int)MlowMath.GoRound(harmHz * 2.0f * invF2StepHz);
            if (harmIx >= cache.Length)
            {
                return RecomputeHarmonicity(harmHz, invF2StepHz, f2w);
            }

            if (cache[harmIx] > HarmonicityUndef)
            {
                return cache[harmIx];
            }

            float hs = RecomputeHarmonicity(harmHz, invF2StepHz, f2w);
            cache[harmIx] = hs;
            return hs;
        }

        private static float RecomputeHarmonicity(float harmHz, float invF2StepHz, float[] f2w)
        {
            float harmWidth = harmHz * invF2StepHz;
            float harmStrength = 0.1f;
            if (harmWidth > 1.97f)
            {
                var peakValleyMags = new float[2 * NumHarms + 1];
                float invHarmWidth = 1.0f / harmWidth;
                var weights = new float[20];
                for (int numHarm = 0; numHarm < peakValleyMags.Length; numHarm++)
                {
                    float ixStart = 0.5f * (float)numHarm * harmWidth;
                    float ixEnd = ixStart + harmWidth;
                    int idxStart = (int)Math.Ceiling(ixStart);
                    int idxEnd = (int)Math.Floor(ixEnd);
                    int weightsLen = idxEnd - idxStart + 1;
                    if (weightsLen < 0)
                    {
                        weightsLen = 0;
                    }

                    Array.Clear(weights, 0, weights.Length);
                    for (int i = 0; i < weightsLen && i < weights.Length; i++)
                    {
                        float tmp = ((float)idxStart - ixStart + (float)i) * invHarmWidth;
                        tmp -= tmp * tmp;
                        weights[i] = tmp * tmp;
                    }

                    int b = idxStart;
                    if (b < 0)
                    {
                        b = 0;
                    }

                    if (b > f2w.Length)
                    {
                        b = f2w.Length;
                    }

                    int avail = f2w.Length - b;
                    if (avail > weightsLen)
                    {
                        avail = weightsLen;
                    }

                    float peakValleyNrg = VuvDot(f2w, b, weights, avail) / VuvSum(weights, weightsLen);
                    peakValleyMags[numHarm] = (float)Math.Sqrt(peakValleyNrg + 1e-30f);
                }

                var magRatiosLog = new float[NumHarms];
                var magWeights = new float[NumHarms];
                for (int numHarm = 0; numHarm < NumHarms; numHarm++)
                {
                    float magPeak = MagPeakW[0] * peakValleyMags[2 * numHarm]
                        + MagPeakW[1] * peakValleyMags[2 * numHarm + 1]
                        + MagPeakW[2] * peakValleyMags[2 * numHarm + 2];
                    float magValley = MagValleyW[0] * peakValleyMags[2 * numHarm]
                        + MagValleyW[1] * peakValleyMags[2 * numHarm + 1]
                        + MagValleyW[2] * peakValleyMags[2 * numHarm + 2];
                    magRatiosLog[numHarm] = (float)Math.Log(magPeak / magValley);
                    magWeights[numHarm] = (float)Math.Sqrt(magPeak + magValley + 1e-30f);
                }

                harmStrength = VuvDot(magWeights, 0, magRatiosLog, NumHarms) / VuvSum(magWeights, NumHarms);
            }

            return harmStrength;
        }

        /// <summary>The C F2w: F2[i] * (i+3), with F2w[0]=F2w[1]=0.</summary>
        public static float[] BuildF2w(float[] f2)
        {
            var f2w = new float[SmplLpc.FLen];
            for (int i = 2; i < SmplLpc.FLen; i++)
            {
                f2w[i] = f2[i] * (float)(i + 3);
            }

            return f2w;
        }

        /// <summary>Harmonicity at avgLag with a fresh cache (the call right after the pitch search).</summary>
        public static float HarmStrengthAt(float avgLag, float[] f2w)
        {
            var cache = new float[HarmCacheLen];
            return SpectralHarmonicity(avgLag, f2w, cache, true);
        }

        /// <summary>
        /// Combines the five voicing strengths + hysteresis into the voicing strength; mutates vuv.
        /// lags is the per-lag-subframe pitch lag in samples; f2 is the power spectrum F2[0..256].
        /// </summary>
        public static float SmplGetSignalMode(float pitchcorr, float[] lags, float avgLag, float harmStrength, float[] f2, float spActProb, VuvMode vuv)
        {
            float pc = pitchcorr;
            if (pc < 0.0f)
            {
                pc = 0.0f;
            }
            else if (pc > 1.0f)
            {
                pc = 1.0f;
            }

            float corrStrength = InvSigmoid(0.1f + 0.75f * pc);
            float vadStrength = 0.04f * (1.0f - 1.04f / (spActProb + 0.04f));

            float nrgLo = 0;
            for (int i = 2; i < TransitionIx; i++)
            {
                float tmp = f2[i] * (float)(i + 3);
                nrgLo += tmp * (float)(TransitionIx - i);
            }

            float nrgHi = 0;
            for (int i = TransitionIx; i < SmplLpc.FLen; i++)
            {
                float tmp = f2[i] * (float)(i + 3);
                nrgHi += tmp * (float)(i - TransitionIx);
            }

            if (vadStrength < -0.1f)
            {
                float smthCoef = -0.5f * vadStrength;
                vuv.NrgLoBgn += smthCoef * (nrgLo - vuv.NrgLoBgn);
                vuv.NrgHiBgn += smthCoef * (nrgHi - vuv.NrgHiBgn);
            }

            float loDiff = nrgLo - vuv.NrgLoBgn;
            if (loDiff < 0.0f)
            {
                loDiff = 0.0f;
            }

            float hiDiff = nrgHi - vuv.NrgHiBgn;
            if (hiDiff < 0.0f)
            {
                hiDiff = 0.0f;
            }

            float tiltLin = (loDiff - hiDiff) / (nrgLo + nrgHi + 1e-9f);
            float tiltStrength = tiltLin * tiltLin * tiltLin;
            float lagStrength = -SmplNoise.SmplSigmoid(0.25f * (38.0f - avgLag));

            float voicingStrength = (VuvWeights[0] * corrStrength
                + VuvWeights[1] * vadStrength
                + VuvWeights[2] * tiltStrength
                + VuvWeights[3] * harmStrength
                + VuvWeights[4] * lagStrength)
                / VuvSum(VuvWeights, 5) + VuvBias;

            if (vuv.LastLagPrev > 0.0f)
            {
                float tmp = (float)MlowMath.Log2(lags[0] / vuv.LastLagPrev);
                if (tmp > 0.0f)
                {
                    tmp *= 0.5f;
                }

                vuv.VoicingPrev /= 0.4f + tmp * tmp;
            }

            voicingStrength += vuv.VoicingPrev * VuvHyst;
            vuv.VoicingPrev = (float)Math.Tanh(3.0f * voicingStrength);
            vuv.LastLagPrev = lags[lags.Length - 1];
            return voicingStrength;
        }
    }
}
