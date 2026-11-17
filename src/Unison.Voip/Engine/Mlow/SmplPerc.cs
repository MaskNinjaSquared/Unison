// =============================================================================
// SmplPerc
//
// Encoder perceptual weighting: FFT-based perceptual autocorrelation with the
// bidirectional mel-width masking smooth, the perceptual LPC response
// (pre-emphasis MA2 + Levinson), and the per-subframe bitrate controller
// (pulse budget + subframe importance).
// Ports: meowcaller mlow/perc.go (MIT)
// =============================================================================
using System;

namespace Unison.Voip.Engine.Mlow
{
    /// <summary>Buffer history (PERCW_NFFT) carried across SmplPercModel calls.</summary>
    internal sealed class PercModelState
    {
        internal readonly float[] Buf = new float[SmplPerc.PercwNfft];
        internal readonly float[] Smthcoef;
        internal readonly float[] BufWin = new float[SmplPerc.PercwNfft];
        internal readonly float[] F = new float[SmplPerc.PercwNfft];

        public PercModelState()
        {
            float fsStep = (SmplPerc.PercwFsKhz * 1000.0f) / (float)SmplPerc.PercwNfft;
            Smthcoef = new float[SmplPerc.PercwNfft / 2 + 1];
            for (int i = 0; i < Smthcoef.Length; i++)
            {
                float percWidthPerBin = SmplPerc.PercMaskSmth * (fsStep * (float)i + SmplPerc.PercMelFcHz) / fsStep;
                Smthcoef[i] = percWidthPerBin / (percWidthPerBin + 1.0f);
            }
        }
    }

    /// <summary>The smpl_EncControlStruct fields the bitrate controller reads.</summary>
    internal sealed class BitrateControllerInputs
    {
        public int InternalSampleRate;
        public int PayloadSizeMs;
        public int FecBitRate;
        public int MainBitRate;
        public int Complexity;
        public int UseFecRateCompensation;
        public int UseDtx;
        public float SubFrameImportanceFactor;
    }

    /// <summary>Bitrate controller state carried across frames.</summary>
    internal sealed class BitrateController
    {
        private int _prevVoiced;
        private float _rateContWnrgSmth;
        private readonly float[] _rateContBitrateScale = new float[SmplPerc.SmplCelpMaxRates];
        private readonly float[] _rateContBitrate = new float[SmplPerc.SmplCelpMaxRates];
        private readonly float[] _adjustmentFactor = { 1.0f, 1.0f };

        /// <summary>bitrate_controller: fills maxPulsesPerSubfr and subfrImportance per rate.</summary>
        public void Control(
            BitrateControllerInputs enc,
            int dtxSidFrame,
            int codedAsActiveVoice,
            float spActProb,
            float nonflatness,
            float voicingStrength,
            int voiced,
            float wnrg,
            float wnrgNext,
            int lowRate,
            int framelen,
            int subfrlen,
            short[] maxPulsesPerSubfr,
            float[] subfrImportance)
        {
            int bweBitrate = 0;
            if (enc.InternalSampleRate > 16000)
            {
                bweBitrate += lowRate != 0 ? 450 : 750;
                if (enc.PayloadSizeMs == 10)
                {
                    bweBitrate += 450;
                }
            }

            _rateContWnrgSmth += 0.6f * (wnrg - _rateContWnrgSmth);

            int framelenIdx = 3;
            switch (enc.PayloadSizeMs)
            {
                case 10:
                    framelenIdx = 0;
                    break;
                case 20:
                    framelenIdx = 1;
                    break;
                case 60:
                    framelenIdx = 2;
                    break;
            }

            maxPulsesPerSubfr[0] = 0;
            maxPulsesPerSubfr[1] = 0;
            subfrImportance[0] = 0;
            subfrImportance[1] = 0;

            int startR = 0;
            if ((SmplPerc.SmplCelpIdxFec + (enc.FecBitRate == 0 ? 1 : 0)) != 0 || enc.FecBitRate == enc.MainBitRate)
            {
                startR = 1;
            }

            int lrIdx = lowRate != 0 ? 0 : 1;

            for (int r = startR; r <= SmplPerc.SmplCelpIdxMain; r++)
            {
                float bitRate = r == SmplPerc.SmplCelpIdxFec ? (float)enc.FecBitRate : (float)enc.MainBitRate;
                if (bitRate > 30000.0f)
                {
                    bitRate = 30000.0f;
                }

                float rateKbps = (bitRate - (float)bweBitrate) / 1000.0f;
                if (lowRate == 0)
                {
                    switch (enc.Complexity)
                    {
                        case 1:
                        case 2:
                            rateKbps *= 0.9900990f;
                            break;
                        case 3:
                        case 4:
                            rateKbps *= 1.0101010f;
                            break;
                    }
                }

                float pulsesPer20msTargetMax;
                float rateControlThrs = SmplPerc.RateControlThrsComp5[framelenIdx][lrIdx];
                if ((bitRate - (float)bweBitrate) < rateControlThrs)
                {
                    pulsesPer20msTargetMax = 1.0f;
                }
                else
                {
                    float[] coeff = SmplPerc.RateControlModelComp5[framelenIdx][lrIdx];
                    if (r == SmplPerc.SmplCelpIdxFec && lowRate == 0 && enc.UseFecRateCompensation != 0)
                    {
                        pulsesPer20msTargetMax = MlowMath.MaxF32(SmplPerc.Bitrate2PulsesHrFec(rateKbps, coeff, rateControlThrs), 1.0f);
                    }
                    else
                    {
                        pulsesPer20msTargetMax = MlowMath.MaxF32(SmplPerc.Bitrate2Pulses(rateKbps, coeff), 1.0f);
                    }
                }

                float relPulserate = pulsesPer20msTargetMax / 16.0f * (320.0f / (float)framelen);
                float relPulserateLog = (float)Math.Log(relPulserate);
                if (_rateContBitrate[r] != bitRate)
                {
                    _rateContBitrateScale[r] = (float)SmplPerc.SmplRateContScale * relPulserate * (1.0f + 0.4f * relPulserateLog * relPulserateLog);
                    _rateContBitrate[r] = bitRate;
                }

                int numsubfrs = framelen / subfrlen;
                int mpps = 1 + (int)MlowMath.GoRound(pulsesPer20msTargetMax * (1.0f + 0.5f) / (float)numsubfrs);
                if (enc.UseDtx != 0 && dtxSidFrame != 0)
                {
                    mpps = 0;
                }
                else
                {
                    mpps = (int)MlowMath.GoRound((float)mpps * (0.5f + 0.5f * (float)Math.Sqrt(spActProb + 1e-12f)));
                    int frameType = SmplPerc.FrameBackgroundNoise;
                    if (codedAsActiveVoice != 0)
                    {
                        frameType = voiced == 1 ? SmplPerc.FrameVoiced : SmplPerc.FrameUnvoiced;
                    }

                    int maxPulses = SmplPerc.MaxPulsesPerFrame[lowRate][frameType] * framelen / 320;
                    int m = maxPulses / numsubfrs;
                    if (mpps > m)
                    {
                        mpps = m;
                    }
                }

                maxPulsesPerSubfr[r] = (short)mpps;

                float imp = (wnrg + 0.01f * wnrgNext) / (_rateContWnrgSmth + 0.02f * wnrgNext + 1e-12f);
                if (voiced != 0)
                {
                    if (bitRate <= 9000.0f)
                    {
                        imp = (float)Math.Sqrt(imp + 1e-12f);
                    }
                }
                else
                {
                    imp *= 0.9f + 0.3f * SmplNoise.SmplSigmoid(nonflatness - 2.0f);
                    imp *= 0.8f;
                }

                if (voiced != _prevVoiced)
                {
                    imp *= 1.1f;
                }

                imp *= 0.9f + 0.3f * 1.0f / (1.0f + 25.0f * voicingStrength * voicingStrength);

                float impFactor = enc.SubFrameImportanceFactor;
                if (impFactor <= 1.0f)
                {
                    imp *= (1.0f - impFactor) + impFactor * (float)Math.Sqrt(spActProb + 1e-12f);
                }
                else if (impFactor <= 2.0f)
                {
                    impFactor -= 1.0f;
                    imp *= (1.0f - impFactor) + impFactor * spActProb;
                }
                else
                {
                    impFactor -= 2.0f;
                    imp *= (1.0f - impFactor) + impFactor * spActProb * spActProb;
                }

                imp *= _adjustmentFactor[r] * _rateContBitrateScale[r];
                subfrImportance[r] = imp;
                _prevVoiced = voiced;
            }
        }
    }

    internal static class SmplPerc
    {
        public const int PercwNfft = 512 + 64;
        public const float PercwFsKhz = 16.0f;
        public const float PercMaskSmth = 0.1158f;
        public const float PercMelFcHz = 320.0f;

        private const int WinNextWbLen = 16 * 2;
        private const int WinNextWbLongLen = 16 * 4;
        private const int Win3ShortLen = WinNextWbLen;
        private const int Win3LongLen = WinNextWbLongLen;
        private const int WinPrevPercLen = 16 * 12;
        private const int PercWin110msLen = 192;
        private const int PercWin120msLen = 352;

        public const int SmplMaxLResp = 32 + 1;
        public const int SmplMaxSfLen = 16 * 10;
        public const int SmplPercRespLen = 16 * 2;

        public const float SmplPercReg = 1e-3f;
        private const float SmplE = 2.7182818284590f;

        public const int FrameBackgroundNoise = 0;
        public const int FrameUnvoiced = 1;
        public const int FrameVoiced = 2;

        public const int SmplCelpIdxFec = 0;
        public const int SmplCelpIdxMain = 1;
        public const int SmplCelpMaxRates = SmplCelpIdxMain + 1;
        public const int SmplMaxPulsesPerSf = 40;
        public const float SmplRateContScale = 26.0f;

        public static readonly float[] SmplPercEmphV = { -0.72f, -0.77f };
        public static readonly float[] SmplPercEmphUV = { -0.55f, -0.6f };

        internal static readonly int[][] MaxPulsesPerFrame = { new[] { 80, 160, 160 }, new[] { 16, 32, 32 } };

        internal static readonly float[][][] RateControlModelComp5 =
        {
            new[]
            {
                new[] { 5.166876656946171f, -8.981699804753452f, 0.07280811614105594f, 0.1301196310618402f, -0.01597680442864421f, 1.7601470147884113f, -3.8161195433141755f, 0.3038629198331684f },
                new[] { -71.71229978402292f, 14.197572549553076f, -0.9863630205846172f, 0.032124893286072924f, -0.0003538411576874928f, 1.803705259861388e-11f, 10.0f, 1.2454667523627154f },
            },
            new[]
            {
                new[] { 32.5371190670542f, -41.270234279452104f, 10.490270829170875f, -1.102121269442237f, 0.03848319274046071f, 3.405326741403831f, -5.102658181889428f, 0.2141935195026695f },
                new[] { -177.10486363500775f, 43.952329593498376f, -3.7049735533247454f, 0.14239771116996938f, -0.001919963993993193f, 7.953695588409639e-6f, 5.220317075476664f, 0.6435364076926223f },
            },
            new[]
            {
                new[] { -79.2663194911617f, 45.00981883522089f, -10.063311543498518f, 1.2311531056576501f, -0.06023559069137118f, 0.059204788212259364f, 3.033961466462233f, 1.0111383197827808f },
                new[] { -122.04861900525415f, 31.62096398905459f, -2.613237037423586f, 0.10050433143234094f, -0.0013233009240188039f, 2.14859438836692e-7f, 1.9077791307787761f, 0.7059420500333776f },
            },
            new[]
            {
                new[] { -182.64255084224325f, 122.90780796179816f, -31.308790671748525f, 3.7850563849431462f, -0.1750480676903051f, 0.05399618467364628f, 3.009451055091342f, 1.1243365512229038f },
                new[] { -132.4565456943888f, 34.361297004632966f, -2.7956546289118887f, 0.10428149547078584f, -0.001322667891395693f, 2.678747426340249e-6f, 6.9940208056381925f, 0.7551244069345737f },
            },
        };

        internal static readonly float[][] RateControlThrsComp5 =
        {
            new[] { 7500f, 10000f },
            new[] { 4500f, 5750f },
            new[] { 4000f, 5000f },
            new[] { 4000f, 4750f },
        };

        private static readonly float[] PercWin110ms = SmplLpc.GenSinWin(PercWin110msLen);
        private static readonly float[] PercWin120ms = SmplLpc.GenSinWin(PercWin120msLen);
        private static readonly float[] Win3Short = SmplLpc.GenCosWin(Win3ShortLen);
        private static readonly float[] Win3Long = SmplLpc.GenCosWin(Win3LongLen);

        /// <summary>smpl_filt_ma2: 2nd-order MA (may be non-monic) over x[xOff..]; state is state[0..2].</summary>
        private static void PercFiltMa2(float[] x, int xOff, int n, float[] coef, float s0, float s1, float[] y)
        {
            if (coef[0] == 1.0f)
            {
                for (int i = 0; i < n - 1; i++)
                {
                    y[1 + i] = x[xOff + 1 + i] + coef[1] * x[xOff + i];
                }
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    y[i] = x[xOff + i] * coef[0];
                }

                for (int i = 0; i < n - 1; i++)
                {
                    y[1 + i] += coef[1] * x[xOff + i];
                }
            }

            for (int i = 0; i < n - 2; i++)
            {
                y[2 + i] += coef[2] * x[xOff + i];
            }

            y[0] = coef[0] * x[xOff] + coef[1] * s0 + coef[2] * s1;
            y[1] += coef[2] * s0;
        }

        private static void PercAc2Rc(float[] corr, int order, float reg, float[] rc)
        {
            var corrDbl = new double[order + 1];
            for (int i = 0; i < order + 1; i++)
            {
                corrDbl[i] = corr[i];
            }

            SmplLpc.Ac2RcDbl(corrDbl, order, 1.0 + (double)reg, rc);
        }

        /// <summary>Inverse real FFT from the ordered REAL layout, unnormalized.</summary>
        public static void RfftBackwardOrdered(float[] f, float[] time)
        {
            int n = f.Length;
            Cpx[][] w = MlowFft.Work(n);
            Cpx[] spec = w[0];
            spec[0] = new Cpx(f[0], 0);
            spec[n / 2] = new Cpx(f[1], 0);
            for (int i = 1; i < n / 2; i++)
            {
                float re = f[2 * i];
                float im = f[2 * i + 1];
                spec[i] = new Cpx(re, im);
                spec[n - i] = new Cpx(re, -im);
            }

            Cpx[] tout = w[1];
            MlowFft.Cfft(spec, tout, 1.0f);
            for (int i = 0; i < n; i++)
            {
                time[i] = tout[i].Re;
            }
        }

        private static void SmplWindowPerc(float[] input, int inOff, float[] o, int oOff, int length, int frameMs, bool useLongWin)
        {
            int win1len = PercWin120msLen;
            float[] win1 = PercWin120ms;
            if (frameMs == 10)
            {
                win1len = PercWin110msLen;
                win1 = PercWin110ms;
            }

            int win3len = useLongWin ? Win3LongLen : Win3ShortLen;
            float[] win3 = useLongWin ? Win3Long : Win3Short;

            for (int i = 0; i < win1len; i++)
            {
                o[oOff + i] = win1[i] * input[inOff + i];
            }

            int mid = length - win1len - Win3LongLen;
            Array.Copy(input, inOff + win1len, o, oOff + win1len, mid);
            int tail = length - Win3LongLen;
            for (int i = 0; i < win3len; i++)
            {
                o[oOff + tail + i] = win3[i] * input[inOff + tail + i];
            }

            if (!useLongWin)
            {
                for (int i = length - Win3LongLen + Win3ShortLen; i < length; i++)
                {
                    o[oOff + i] = 0.0f;
                }
            }
        }

        private static void SmthFilt(float[] f, float[] smthcoef)
        {
            const int half = PercwNfft / 2;
            float f2smth = f[0];
            for (int i = 1; i < half; i++)
            {
                float f2new = f[2 * i];
                f2smth = f2new + smthcoef[i] * (f2smth - f2new);
                f[2 * i] = f2smth;
            }

            f[1] = f[1] + smthcoef[half] * (f2smth - f[1]);
            f2smth = f[1];
            for (int i = half - 1; i > 0; i--)
            {
                float f2new = f[2 * i];
                f2smth = f2new + smthcoef[i] * (f2smth - f2new);
                f[2 * i] = f2smth;
            }

            f[0] = f[0] + smthcoef[0] * (f2smth - f[0]);
        }

        /// <summary>
        /// Windowed power spectrum -> masking smooth -> inverse -> 1/NFFT scale; returns the
        /// first lenR autocorrelation lags. The state buffer advances as in the C.
        /// </summary>
        public static float[] SmplPercModel(PercModelState state, float[] xsubfr, int xsubfrLen, int frameMs, int isLastSubfr, int lenR)
        {
            float[] buf = state.Buf;
            int srcOff = xsubfrLen - (WinNextWbLongLen - WinNextWbLen);
            int keep = PercwNfft - xsubfrLen;
            Array.Copy(buf, srcOff, buf, 0, keep);
            Array.Copy(xsubfr, 0, buf, keep, xsubfrLen);

            int winlen = WinPrevPercLen + frameMs * 16 + Win3LongLen;
            int skipSamples = PercwNfft - winlen;

            float[] bufWin = state.BufWin;
            Array.Clear(bufWin, 0, bufWin.Length);
            SmplWindowPerc(buf, skipSamples, bufWin, skipSamples, winlen, frameMs, isLastSubfr == 0);

            float[] f = state.F;
            MlowFft.RfftForwardOrdered(bufWin, f);
            f[0] = f[0] * f[0];
            f[1] = f[1] * f[1];
            for (int i = 1; i < PercwNfft / 2; i++)
            {
                f[2 * i] = f[2 * i] * f[2 * i] + f[2 * i + 1] * f[2 * i + 1];
                f[2 * i + 1] = 0.0f;
            }

            SmthFilt(f, state.Smthcoef);
            RfftBackwardOrdered(f, bufWin);

            var r = new float[lenR];
            float g = 1.0f / (float)PercwNfft;
            for (int i = 0; i < lenR; i++)
            {
                r[i] = bufWin[i] * g;
            }

            return r;
        }

        /// <summary>MA2 (b={pe, 1+pe^2, pe}) on R[1..] then Levinson + rc2a: A[0..percRespLen).</summary>
        public static float[] SmplPercAc2a(float[] r, int lenR, float percEmph, int percRespLen, float reg)
        {
            var b = new[] { percEmph, 1.0f + percEmph * percEmph, percEmph };
            var rTmp = new float[SmplMaxLResp];
            PercFiltMa2(r, 1, percRespLen, b, r[0], r[1], rTmp);

            var rc = new float[SmplMaxLResp];
            PercAc2Rc(rTmp, percRespLen - 1, reg, rc);

            var a = new float[percRespLen];
            SmplLpc.Rc2A(rc, percRespLen - 1, a);
            return a;
        }

        internal static float Bitrate2Pulses(float rateKbps, float[] coeff)
        {
            return coeff[0]
                + coeff[1] * rateKbps
                + coeff[2] * rateKbps * rateKbps
                + coeff[3] * (float)Math.Pow(rateKbps, 3.0)
                + coeff[4] * (float)Math.Pow(rateKbps, 4.0)
                + coeff[5] * (float)Math.Pow(SmplE, (rateKbps - coeff[6]) * coeff[7]);
        }

        internal static float Bitrate2PulsesHrFec(float rateKbps, float[] coeff, float onePulseRateBps)
        {
            const float rateThresKbps = 9.0f;
            if (rateKbps >= rateThresKbps)
            {
                return Bitrate2Pulses(rateKbps, coeff);
            }

            if (onePulseRateBps >= rateThresKbps * 1000.0f)
            {
                return 1.0f;
            }

            float pulsesThres = Bitrate2Pulses(rateThresKbps, coeff);
            float sc = (rateThresKbps - rateKbps) / (rateThresKbps - onePulseRateBps / 1000.0f);
            return pulsesThres - sc * (pulsesThres - 1.0f);
        }
    }
}
