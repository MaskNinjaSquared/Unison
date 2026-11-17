// =============================================================================
// SmplAnalysis
//
// Encoder analysis, PCM -> SmplFrameParams. Per 20 ms internal frame: LPC
// front-end (window -> FFT autocorrelation -> A / NLSF), bit-exact LSF
// quantizer, perceptual model + pitch estimator + voicing classifier, CELP
// excitation, then the voiced (LTP) / unvoiced (nrgres) / silent candidate.
// Cross-packet history lives in SmplEncoderState.
// Ports: meowcaller mlow/analysis.go (MIT)
// =============================================================================
using System;

namespace Unison.Voip.Engine.Mlow
{
    /// <summary>Cross-frame analysis history plus the persistent sub-models and per-packet work buffers.</summary>
    internal sealed class SmplEncoderState
    {
        internal double[] Hist;
        internal float[] HpMA;
        internal float[] HpAR;
        internal readonly float[] HpState = new float[4];
        internal CelpEncoder Celp;
        internal PercModelState Perc;
        internal float[] PercPrev;
        internal BitrateController Bitrate;
        internal float[] LpcHist;
        internal float[] PrevLsfq;
        internal bool PrevVoiced;
        internal SmplVadState Vad;
        internal readonly VuvMode Vuv = new VuvMode();
        internal float[] HpPitchHist;
        internal float[] LtpBuf;
        internal readonly PitchEstState PitchEst = new PitchEstState();

        internal readonly PitchEncScratch PitchScratch = new PitchEncScratch();
        internal readonly short[] PcmI16 = new short[SmplAnalysis.Need];
        internal readonly float[] Hp = new float[SmplAnalysis.Need];
        internal readonly double[] X = new double[SmplSynth.SmplOrder + SmplAnalysis.Need];
        internal readonly float[] Xn = new float[SmplAnalysis.ResLead + SmplAnalysis.Need];
        internal readonly float[] HpFull = new float[SmplAnalysis.LpcHistLen + SmplAnalysis.Need + SmplAnalysis.WinnextWbLen];
        internal readonly float[] HpPitchPrev = new float[SmplAnalysis.PitchLagMax];
        internal readonly float[] LpcBuf = new float[SmplLpc.LpcBufLen];
        internal readonly float[] Windowed = new float[SmplLpc.LpcBufLen];
        internal readonly float[] XSubfr = new float[2 * SmplSynth.SmplSubfrLen + 32];
        internal readonly float[] ResSf = new float[SmplSynth.SmplSubfrLen];
        internal readonly short[] Numsurv = new short[SmplPerc.SmplMaxPulsesPerSf];
        internal readonly short[] MaxPulses = new short[SmplPerc.SmplCelpMaxRates];
        internal readonly float[] Importance = new float[SmplPerc.SmplCelpMaxRates];
        internal readonly float[] WinNVoiced = new float[SmplSynth.SmplOrder + SmplSynth.SmplIntfLen];
    }

    /// <summary>Per-internal-frame CELP / perceptual context.</summary>
    internal sealed class CelpFrameCtx
    {
        public SmplEncoderState Es;
        public int Intf;
        public float SpActProb;
        public bool CodedAsActiveVoice;
        public float[] F2;
        public float VoicingStrength;
        public float[][] PercCorrs;
        public readonly float[][] BlockLags = { new float[2], new float[2], new float[2], new float[2] };
    }

    /// <summary>The front-end LPC/NLSF of one internal frame plus the cond-coding inputs.</summary>
    internal sealed class FrontEndLsf
    {
        public float[] A;
        public float[] Nlsf;
        public float[] PrevLsfq;
        public bool PrevVoiced;
        public int Intf;
    }

    internal sealed class SmplCandidate
    {
        public SmplInternalParams Ip;
        public int Stage1;
        public float[] Nlsf;
    }

    internal static class SmplAnalysis
    {
        internal const int Need = SmplSynth.SmplIntfLen * 3;
        internal const int LpcHistLen = 144;
        private const int LpcPre = 96;
        private const int LsfSurv = 6;
        internal const int WinnextWbLen = 32;
        private const float LsfRdwAdj = 1.1952286f;
        internal const int ResLead = SmplSynth.SmplOrder + WinnextWbLen;

        private const int MainBitRate = 20000;
        private const int Complexity = 8;

        private const bool CelpLowRate = false;
        private const int CelpPercRespLen = 32;
        private const int CelpFcbSubfrlen = 80;
        private const int CelpSubfrPerPacket = 12;
        private const int PercRLen = CelpPercRespLen + 1;
        private const int FcbTotSurv20msMax = 100;
        private const float EncHpFcornerHz = 35.0f;

        private const float PercEmphPitch = -0.82f;
        private const int PitchPercRespLen = 17;
        internal const int PitchLagMax = 320;
        private const int PitchLookaheadLen = 7;
        private const double VoicedNormGain = 1.0;

        private const int SubfrCount = SmplSynth.SmplSubfrCount;
        private const int SubfrLen = SmplSynth.SmplSubfrLen;
        private const int IntfLen = SmplSynth.SmplIntfLen;
        private const int Order = SmplSynth.SmplOrder;

        private static readonly float[] PitchEmph = { PercEmphPitch, PercEmphPitch };

        private static readonly BitrateControllerInputs EncInputs = new BitrateControllerInputs
        {
            InternalSampleRate = 16000,
            PayloadSizeMs = 60,
            FecBitRate = 0,
            MainBitRate = MainBitRate,
            Complexity = Complexity,
            UseFecRateCompensation = 0,
            UseDtx = 0,
            SubFrameImportanceFactor = 1.0f,
        };

        private static readonly Lazy<int[]> SilentGains = new Lazy<int[]>(() => RateControlGains(0.0));

        /// <summary>Turns one 60 ms frame (960 float samples at 16 kHz, ~[-1,1]) into frame parameters.</summary>
        public static SmplFrameParams AnalyzeFrame(SmplEncoderState es, float[] pcm)
        {
            SmplSynthTables synthT = SmplSynth.LoadSmplSynthTables();

            short[] pcmI16 = es.PcmI16;
            for (int i = 0; i < Need; i++)
            {
                double v = MlowMath.GoRound(pcm[i] * 32768.0f);
                if (v > 32767)
                {
                    v = 32767;
                }

                if (v < -32768)
                {
                    v = -32768;
                }

                pcmI16[i] = (short)v;
            }

            if (es.Vad == null)
            {
                es.Vad = new SmplVadState();
            }

            VadPacketResult vad = es.Vad.ProcessPacket(pcmI16, IntfLen);

            if (es.HpMA == null)
            {
                SmplPostfilter.SmplGetHpCoefs(EncHpFcornerHz, out es.HpMA, out es.HpAR);
            }

            float[] hp = es.Hp;
            SmplPostfilter.SmplFiltArma2(pcm, Need, es.HpMA, es.HpAR, es.HpState, hp);

            double[] x = es.X;
            Array.Clear(x, 0, x.Length);
            if (es.Hist != null && es.Hist.Length >= Order)
            {
                Array.Copy(es.Hist, es.Hist.Length - Order, x, 0, Order);
            }

            for (int i = 0; i < Need; i++)
            {
                x[Order + i] = (double)hp[i] * 32768.0;
            }

            if (es.Celp == null)
            {
                es.Celp = new CelpEncoder(CelpLowRate, CelpPercRespLen, CelpFcbSubfrlen, CelpSubfrPerPacket);
            }

            if (es.Perc == null)
            {
                es.Perc = new PercModelState();
            }

            if (es.Bitrate == null)
            {
                es.Bitrate = new BitrateController();
            }

            if (es.PercPrev == null || es.PercPrev.Length != PercRLen)
            {
                es.PercPrev = new float[PercRLen];
            }

            float[] xn = es.Xn;
            Array.Clear(xn, 0, xn.Length);
            if (es.Hist != null && es.Hist.Length >= ResLead)
            {
                for (int i = 0; i < ResLead; i++)
                {
                    xn[i] = (float)(es.Hist[es.Hist.Length - ResLead + i] / 32768.0);
                }
            }

            Array.Copy(hp, 0, xn, ResLead, Need);

            float[] hpFull = es.HpFull;
            Array.Clear(hpFull, 0, hpFull.Length);
            if (es.LpcHist != null && es.LpcHist.Length == LpcHistLen)
            {
                Array.Copy(es.LpcHist, hpFull, LpcHistLen);
            }

            Array.Copy(hp, 0, hpFull, LpcHistLen, Need);

            float[] hpPitchHist = es.HpPitchPrev;
            if (es.HpPitchHist != null && es.HpPitchHist.Length == PitchLagMax)
            {
                Array.Copy(es.HpPitchHist, hpPitchHist, PitchLagMax);
            }
            else
            {
                Array.Clear(hpPitchHist, 0, PitchLagMax);
            }

            if (es.HpPitchHist == null)
            {
                es.HpPitchHist = new float[PitchLagMax];
            }

            Array.Copy(hp, Need - PitchLagMax, es.HpPitchHist, 0, PitchLagMax);

            if (es.LtpBuf == null || es.LtpBuf.Length != SmplPitch.MaxLtpBufLen)
            {
                es.LtpBuf = new float[SmplPitch.MaxLtpBufLen];
            }

            float[] prevLsfq = es.PrevLsfq;
            bool prevVoiced = es.PrevVoiced;
            float[] prevNlsf = null;
            var lstate = new SmplLsfState();

            var fp = new SmplFrameParams { Toc = 0x50, Config = 0 };
            for (int f = 0; f < 3; f++)
            {
                int winOff = f * IntfLen;
                int winNOff = f * IntfLen;

                int lpcStart = LpcHistLen - LpcPre + f * IntfLen;
                Array.Copy(hpFull, lpcStart, es.LpcBuf, 0, SmplLpc.LpcBufLen);
                SmplLpc.SmplWindowLpc20(es.LpcBuf, f < 2, es.Windowed);
                var a = new float[SmplLpc.Order + 1];
                var f2 = new float[SmplLpc.FLen];
                SmplLpc.SmplLpcAnalyzeWithF2(es.Windowed, a, f2);
                float[] nlsf = SmplLpc.SmplA2Nlsf16(a);

                var cs = new CelpFrameCtx
                {
                    Es = es,
                    Intf = f,
                    SpActProb = vad.VadResults[f],
                    CodedAsActiveVoice = vad.CodedAsActiveVoice,
                    F2 = f2,
                };
                var fe = new FrontEndLsf { A = a, Nlsf = nlsf, PrevLsfq = prevLsfq, PrevVoiced = prevVoiced, Intf = f };

                bool voicedOut;
                SmplCandidate chosen = AnalyzeInternal(synthT, lstate, f, x, winOff, xn, winNOff, prevNlsf, fe, cs, out voicedOut);
                prevNlsf = chosen.Nlsf;
                prevLsfq = chosen.Nlsf;
                prevVoiced = voicedOut;
                fp.Internal[f] = chosen.Ip;
                if (f == 2)
                {
                    es.PitchEst.ResetCond();
                }
            }

            if (es.Hist == null)
            {
                es.Hist = new double[Order + WinnextWbLen];
            }

            Array.Copy(x, x.Length - (Order + WinnextWbLen), es.Hist, 0, Order + WinnextWbLen);
            if (es.LpcHist == null)
            {
                es.LpcHist = new float[LpcHistLen];
            }

            Array.Copy(hp, Need - LpcHistLen, es.LpcHist, 0, LpcHistLen);
            es.PrevLsfq = prevLsfq;
            es.PrevVoiced = prevVoiced;
            return fp;
        }

        private static void CopyLsfState(SmplLsfState dst, SmplLsfState src)
        {
            dst.PrevStage1 = src.PrevStage1;
            dst.PrevMatch = src.PrevMatch;
            dst.HavePrev = src.HavePrev;
            dst.PrevGainIdx = src.PrevGainIdx;
            dst.PrevFiltIdx = src.PrevFiltIdx;
            dst.PrevLag = src.PrevLag;
            dst.PrevFracLag = src.PrevFracLag;
            dst.PrevLagSamples = src.PrevLagSamples;
            dst.PrevLagblk = src.PrevLagblk;
            dst.PrevLagidx = src.PrevLagidx;
        }

        /// <summary>Bit-exact LSF quantizer + the cond-coding condition; returns the committed (reconstructed) NLSF.</summary>
        private static float[] Quantize(FrontEndLsf fe, SmplSynthTables synthT, int voiced, float[] prevNlsf, out int grid, int[] stage2)
        {
            bool cond = (fe.PrevVoiced == (voiced != 0)) && fe.Intf > 0;
            LsfQuantResult res = cond && fe.PrevLsfq != null && fe.PrevLsfq.Length == SmplLpc.Order
                ? LsfQuant.LsfQuantCond(fe.A, fe.Nlsf, fe.PrevLsfq, voiced, 0, LsfRdwAdj, LsfSurv)
                : LsfQuant.Quant(fe.A, fe.Nlsf, voiced, 0, LsfRdwAdj, LsfSurv);
            grid = res.Qi[0];
            Array.Copy(res.Qi, 1, stage2, 0, SmplLpc.Order);
            return SmplSynth.SmplReconstructNLSF(synthT, voiced, 0, grid, stage2, prevNlsf);
        }

        private static SmplCandidate AnalyzeInternal(
            SmplSynthTables synthT,
            SmplLsfState lstate,
            int intf,
            double[] x,
            int winOff,
            float[] xn,
            int winNOff,
            float[] prevNlsf,
            FrontEndLsf fe,
            CelpFrameCtx cs,
            out bool isVoiced)
        {
            SmplEncoderState es = cs.Es;
            cs.PercCorrs = ComputePercCorrs(cs);
            BuildLtpBuf(cs, cs.PercCorrs);
            PitchResult pr = SmplPitchEnc.Estimate(es.PitchEst, es.LtpBuf, cs.F2, cs.CodedAsActiveVoice, es.PitchScratch);
            var lags8 = (float[])pr.Lags.Clone();
            float lagSamples = pr.Lags[0];
            float vstr = SmplSignalMode.SmplGetSignalMode(pr.Pitchcorr, lags8, pr.AvgLag, pr.HarmStrength, cs.F2, cs.SpActProb, es.Vuv);
            cs.VoicingStrength = vstr;
            bool isVoicedDecision = vstr > 0.0f && cs.CodedAsActiveVoice;
            lstate.PrevLagSamples = isVoicedDecision ? lagSamples : 0.0f;
            if (!isVoicedDecision)
            {
                es.PitchEst.ResetCond();
                Array.Clear(lags8, 0, lags8.Length);
            }

            var voicedLstate = new SmplLsfState();
            CopyLsfState(voicedLstate, lstate);
            SmplLsf.SmplAdvanceLsfState(voicedLstate, intf, 1);

            SmplCandidate chosen;
            if (isVoicedDecision)
            {
                SmplPitchParams vdPp = VoicedDecisionForLag(pr.BlocksegIdx, pr.Laginds, cs, lags8);
                chosen = VoicedCandidate(synthT, x, winOff, prevNlsf, fe, cs, vdPp);
                isVoiced = true;
            }
            else
            {
                chosen = UnvoicedCandidate(synthT, x, winOff, xn, winNOff, prevNlsf, fe, cs);
                isVoiced = false;
            }

            if (chosen.Stage1 == 1)
            {
                CopyLsfState(lstate, voicedLstate);
                ReplayPitchState(lstate, 4, chosen.Ip.Pulses.Subfr, chosen.Ip.Pitch);
            }
            else
            {
                SmplLsf.SmplAdvanceLsfState(lstate, intf, chosen.Stage1);
            }

            return chosen;
        }

        private static SmplCandidate UnvoicedCandidate(SmplSynthTables synthT, double[] win, int winOff, float[] winN, int winNOff, float[] prevNlsf, FrontEndLsf fe, CelpFrameCtx cs)
        {
            double r0 = 0;
            for (int i = 0; i < IntfLen; i++)
            {
                double v = win[winOff + Order + i];
                r0 += v * v;
            }

            if (r0 <= 0.0)
            {
                var flat = new float[SubfrCount][];
                for (int sf = 0; sf < SubfrCount; sf++)
                {
                    flat[sf] = new float[Order + 1];
                    flat[sf][0] = 1.0f;
                }

                RunCelpSubframes(cs, flat, new float[IntfLen], null, cs.PercCorrs, SmplPerc.SmplPercEmphUV, 0);
                return SilentInternal(synthT, prevNlsf);
            }

            var ip = new SmplInternalParams();
            int bgrid;
            float[] brec = Quantize(fe, synthT, 0, prevNlsf, out bgrid, ip.Lsf.Stage2);
            float[][] predcoefs;
            float[] resLpc;
            int interpolIdx = LsfInterpolSearch(brec, fe.PrevLsfq, winN, winNOff, out predcoefs, out resLpc);

            CelpSubframeOut[] celpOut = RunCelpSubframes(cs, predcoefs, resLpc, null, cs.PercCorrs, SmplPerc.SmplPercEmphUV, 0);

            var pulseVec = new int[IntfLen];
            var fcbgIdx = new int[4];
            const int main = SmplPerc.SmplCelpIdxMain;
            for (int sf = 0; sf < SubfrCount; sf++)
            {
                AccumulatePulses(celpOut[sf].Pulses[main], pulseVec, sf);
                fcbgIdx[sf] = celpOut[sf].GainIdx[main];
            }

            var nrgres = new float[4];
            for (int sf = 0; sf < 4; sf++)
            {
                float e = 0;
                for (int i = 0; i < SubfrLen; i++)
                {
                    float v = resLpc[sf * SubfrLen + i];
                    e += v * v;
                }

                nrgres[sf] = e / (float)SubfrLen;
            }

            NrgResQuant nq = SmplSynth.QuantNrgRes4(nrgres);

            ip.Lsf.Stage1 = 0;
            ip.Lsf.Grid = bgrid;
            ip.Lsf.Extra = interpolIdx;
            BuildPulseParams(pulseVec, ip.Pulses);
            ip.Gains.GainMain = nq.FrameQi;
            ip.Gains.GainDelta = nq.ShapeQi;
            for (int sf = 0; sf < 4; sf++)
            {
                ip.Gains.NrgRes[sf] = ip.Pulses.Subfr[sf] > 0 ? fcbgIdx[sf] : -1;
            }

            return new SmplCandidate { Ip = ip, Stage1 = 0, Nlsf = brec };
        }

        private static void AccumulatePulses(short[] pulses, int[] pulseVec, int sf)
        {
            foreach (short v in pulses)
            {
                int sign = 1 + 2 * (v >> 15);
                int pos = v * sign - 1;
                if (pos >= 0 && pos < SubfrLen)
                {
                    pulseVec[sf * SubfrLen + pos] += sign;
                }
            }
        }

        private static CelpSubframeOut[] RunCelpSubframes(CelpFrameCtx cs, float[][] predcoefs, float[] resLpc, float[][] blockLags, float[][] percCorrs, float[] emph, int voiced)
        {
            SmplEncoderState es = cs.Es;
            float[][] percWght = PercCorrsToWght(percCorrs, emph, CelpPercRespLen);
            var outs = new CelpSubframeOut[SubfrCount];

            var wnrgs = new float[SubfrCount];
            for (int sf = 0; sf < SubfrCount; sf++)
            {
                const float scale = 32768.0f;
                float s = 0;
                for (int i = 0; i < SubfrLen; i++)
                {
                    float v = resLpc[sf * SubfrLen + i];
                    s += (v * scale) * (v * scale);
                }

                wnrgs[sf] = s;
            }

            const int totSurv = 1000 * (FcbTotSurv20msMax * CelpFcbSubfrlen) / (20 * 16000);
            short[] numsurv = es.Numsurv;
            short[] maxPulses = es.MaxPulses;
            float[] importance = es.Importance;
            float[] res = es.ResSf;
            var lags = new float[3];
            for (int sf = 0; sf < SubfrCount; sf++)
            {
                float wnrg = wnrgs[sf];
                float wnrgNext = sf + 1 < SubfrCount ? wnrgs[sf + 1] : wnrgs[sf];
                float nonflatness = voiced != 0 ? 0.0f : 2.0f;
                es.Bitrate.Control(EncInputs, 0, cs.CodedAsActiveVoice ? 1 : 0, cs.SpActProb, nonflatness, cs.VoicingStrength, voiced, wnrg, wnrgNext, 0, 320, 80, maxPulses, importance);
                for (int i = 0; i < numsurv.Length; i++)
                {
                    numsurv[i] = 1;
                }

                CelpEncoder.SmplDistributeFcbSurv(numsurv, maxPulses[1], totSurv);

                if (blockLags != null)
                {
                    lags[0] = blockLags[sf][0];
                    lags[1] = blockLags[sf][1];
                    lags[2] = blockLags[sf][1];
                }
                else
                {
                    lags[0] = 0;
                    lags[1] = 0;
                    lags[2] = 0;
                }

                Array.Copy(resLpc, sf * SubfrLen, res, 0, SubfrLen);
                outs[sf] = es.Celp.EncodeSubframe(res, predcoefs[sf], percWght[sf], lags, importance, maxPulses, numsurv);
            }

            return outs;
        }

        /// <summary>Per-subframe perceptual autocorrelation; advances the perc state exactly once per internal frame.</summary>
        private static float[][] ComputePercCorrs(CelpFrameCtx cs)
        {
            SmplEncoderState es = cs.Es;
            const int frameMs = 20;
            const int shorter = 32;
            var corrs = new float[SubfrCount][];
            float[] hpN = es.Hp;
            float[] xsubfr = es.XSubfr;
            for (int sf = 1; sf < SubfrCount; sf += 2)
            {
                int start = cs.Intf * IntfLen + (sf - 1) * SubfrLen;
                const int xlen = 2 * SubfrLen + shorter;
                for (int i = 0; i < xlen; i++)
                {
                    int idx = start + i;
                    xsubfr[i] = idx < hpN.Length ? hpN[idx] : 0.0f;
                }

                int isLast = cs.Intf == 2 && sf == SubfrCount - 1 ? 1 : 0;
                float[] r = SmplPerc.SmplPercModel(es.Perc, xsubfr, xlen, frameMs, isLast, PercRLen);
                var even = new float[PercRLen];
                for (int i = 0; i < PercRLen; i++)
                {
                    float prev = i < es.PercPrev.Length ? es.PercPrev[i] : 0.0f;
                    even[i] = 0.5f * (r[i] + prev);
                }

                corrs[sf - 1] = even;
                es.PercPrev = r;
                corrs[sf] = r;
            }

            return corrs;
        }

        private static float[][] PercCorrsToWght(float[][] corrs, float[] emph, int respLen)
        {
            int idx = CelpLowRate ? 1 : 0;
            var o = new float[corrs.Length][];
            for (int i = 0; i < corrs.Length; i++)
            {
                o[i] = SmplPerc.SmplPercAc2a(corrs[i], PercRLen, emph[idx], respLen, SmplPerc.SmplPercReg);
            }

            return o;
        }

        private static int LsfInterpolSearch(float[] brec, float[] prevLsfq, float[] winN, int winNOff, out float[][] predcoefs, out float[] resLpc)
        {
            float[][] pc0;
            float[] res0;
            float rms0 = ResidualFor(brec, prevLsfq, winN, winNOff, 0, out pc0, out res0);
            float[][] pc1;
            float[] res1;
            float rms1 = ResidualFor(brec, prevLsfq, winN, winNOff, 1, out pc1, out res1);
            if (rms1 < rms0 * 0.998f)
            {
                predcoefs = pc1;
                resLpc = res1;
                return 1;
            }

            predcoefs = pc0;
            resLpc = res0;
            return 0;
        }

        private static float ResidualFor(float[] brec, float[] prevLsfq, float[] winN, int winNOff, int idx, out float[][] predcoefs, out float[] res)
        {
            predcoefs = SmplLpc.SmplLpcInterpolIdx(brec, prevLsfq, idx);
            res = new float[IntfLen];
            float sumRms = 0;
            for (int sf = 0; sf < SubfrCount; sf++)
            {
                AnalysisResidualSubfr(predcoefs[sf], winN, winNOff, sf, res, sf * SubfrLen);
                float nrg = 0;
                for (int i = 0; i < SubfrLen; i++)
                {
                    float v = res[sf * SubfrLen + i];
                    nrg += v * v;
                }

                sumRms += (float)Math.Sqrt(nrg + 1e-30f);
            }

            return sumRms;
        }

        private static void AnalysisResidualSubfr(float[] aSyn, float[] winN, int winNOff, int sf, float[] res, int resOff)
        {
            for (int n = 0; n < SubfrLen; n++)
            {
                int idx = winNOff + Order + sf * SubfrLen + n;
                float acc = winN[idx];
                for (int j = 1; j <= Order; j++)
                {
                    acc += aSyn[j] * winN[idx - j];
                }

                res[resOff + n] = acc;
            }
        }

        private static SmplCandidate SilentInternal(SmplSynthTables synthT, float[] prevNlsf)
        {
            var ip = new SmplInternalParams();
            for (int k = 0; k < 16; k++)
            {
                ip.Lsf.Stage2[k] = synthT.Valtables[0][0][0][k].Length / 2;
            }

            int[] g = SilentGains.Value;
            ip.Gains.GainMain = g[0];
            ip.Gains.GainDelta = g[1];
            for (int sf = 0; sf < 4; sf++)
            {
                ip.Gains.NrgRes[sf] = -1;
            }

            float[] nlsf = SmplSynth.SmplReconstructNLSF(synthT, 0, 0, ip.Lsf.Grid, ip.Lsf.Stage2, prevNlsf);
            return new SmplCandidate { Ip = ip, Stage1 = 0, Nlsf = nlsf };
        }

        /// <summary>Pulse vector to the structured counts plus the raw magnitude-run / sign symbols the entropy coder replays.</summary>
        internal static void BuildPulseParams(int[] pulse, SmplPulseParams pp)
        {
            const int p3 = 4;
            const int posPer = IntfLen / p3;
            for (int sf = 0; sf < p3; sf++)
            {
                int s = 0;
                for (int n = sf * posPer; n < (sf + 1) * posPer; n++)
                {
                    int a = pulse[n];
                    s += a < 0 ? -a : a;
                }

                pp.Subfr[sf] = s;
            }

            pp.Total = pp.Subfr[0] + pp.Subfr[1] + pp.Subfr[2] + pp.Subfr[3];

            pp.MagRuns.Clear();
            pp.SignSyms.Clear();
            int numPos = 0;
            var signs = new bool[IntfLen];
            for (int sf = 0; sf < p3; sf++)
            {
                if (pp.Subfr[sf] <= 0)
                {
                    continue;
                }

                int basePos = posPer * sf;
                int runPos = basePos;
                bool first = true;
                for (int n = basePos; n < basePos + posPer; n++)
                {
                    if (pulse[n] == 0)
                    {
                        continue;
                    }

                    int magv = pulse[n];
                    int mag = magv < 0 ? -magv : magv;
                    pp.MagRuns.Add(first ? n - basePos : n - runPos);
                    runPos = n;
                    for (int k = 0; k < mag - 1; k++)
                    {
                        pp.MagRuns.Add(0);
                    }

                    signs[numPos++] = magv >= 0;
                    first = false;
                }
            }

            int p = 0;
            while (p < numPos)
            {
                int nbits = numPos - p;
                if (nbits > 15)
                {
                    nbits = 15;
                }

                uint sym = 0;
                for (int q = 0; q < nbits; q++)
                {
                    uint bit = signs[p + q] ? 1u : 0u;
                    sym |= bit << (nbits - 1 - q);
                }

                pp.SignSyms.Add(new SmplRawSym { Sym = sym, Nbits = (uint)nbits });
                p += nbits;
            }
        }

        /// <summary>smpl rate-control gain search against a linear target (main, delta, gainQ).</summary>
        private static int[] RateControlGains(double targetLinear)
        {
            SmplMem mem = SmplMem.Load();
            const uint cfgSel = 2;
            int cb1 = mem.I16(0xf35e0u + cfgSel * 2);
            const uint gainTabAddr = 0xf35f0u;
            double bestD = double.PositiveInfinity;
            int bgm = 0, bgd = 0, bgq = 0;
            for (int gm = 0; gm < 84; gm++)
            {
                int base7 = unchecked(gm * cb1 - 0x154000);
                for (int gd = 0; gd < 98; gd++)
                {
                    int cbv = mem.I16(gainTabAddr + (uint)(4 * gd) * 2);
                    int gq = unchecked(base7 + (cbv << 4));
                    double d = Math.Abs(SmplSynth.SmplGainLin(gq) - targetLinear);
                    if (d < bestD)
                    {
                        bestD = d;
                        bgm = gm;
                        bgd = gd;
                        bgq = gq;
                    }
                }
            }

            return new[] { bgm, bgd, bgq };
        }

        /// <summary>Rolls the persistent weighted-speech buffer and writes this internal frame's weighted speech + lookahead.</summary>
        private static void BuildLtpBuf(CelpFrameCtx cs, float[][] percCorrs)
        {
            SmplEncoderState es = cs.Es;
            float[][] respPitch = PercCorrsToWght(percCorrs, PitchEmph, PitchPercRespLen);
            const int maxLen = SmplPitch.MaxLtpBufLen;
            const int look = PitchLookaheadLen;
            const int framelen = IntfLen;
            float[] ltp = es.LtpBuf;
            const int keep = maxLen - framelen - look;
            Array.Copy(ltp, framelen, ltp, 0, keep);

            int frameStart = cs.Intf * IntfLen - WinnextWbLen;
            float[] hpN = es.Hp;
            float[] hist = es.HpPitchPrev;
            const int wOrigin = maxLen - SubfrCount * SubfrLen - look;
            for (int i = 0; i < SubfrCount; i++)
            {
                float[] coef = respPitch[i];
                for (int n = 0; n < SubfrLen; n++)
                {
                    int pos = i * SubfrLen + n;
                    float res = Sample(hpN, hist, frameStart + pos);
                    for (int j = 1; j < PitchPercRespLen; j++)
                    {
                        res += coef[j] * Sample(hpN, hist, frameStart + pos - j);
                    }

                    ltp[wOrigin + pos] = res;
                }
            }

            float[] c3 = respPitch[SubfrCount - 1];
            for (int n = 0; n < look; n++)
            {
                int pos = framelen + n;
                float res = Sample(hpN, hist, frameStart + pos);
                for (int j = 1; j < PitchPercRespLen; j++)
                {
                    res += c3[j] * Sample(hpN, hist, frameStart + pos - j);
                }

                ltp[maxLen - look + n] = res;
            }
        }

        private static float Sample(float[] hpN, float[] hist, int idx)
        {
            if (idx >= 0)
            {
                return idx < hpN.Length ? hpN[idx] : 0.0f;
            }

            int k = idx + PitchLagMax;
            return k >= 0 ? hist[k] : 0.0f;
        }

        private static void ReplayPitchState(SmplLsfState st, int p3, int[] subfrCounts, SmplPitchParams pp)
        {
            int take = p3 > 4 ? 4 : p3;
            for (int sf = 0; sf < take; sf++)
            {
                st.PrevGainIdx = pp.GainIdx[sf];
                if (subfrCounts[sf] > 0)
                {
                    st.PrevFiltIdx = pp.FiltIdx[sf];
                }
            }

            int nblk, nidx;
            SmplPitch.SmplLagsPredictorAfter(SmplPitch.LoadPitchTables(), pp.BlocksegIdx, pp.Laginds, out nblk, out nidx);
            st.PrevLagblk = nblk;
            st.PrevLagidx = nidx;
        }

        private static SmplPitchParams VoicedDecisionForLag(int blocksegIdx, int[] laginds, CelpFrameCtx cs, float[] lags8)
        {
            for (int b = 0; b < 8; b++)
            {
                float v = (float)laginds[b] * 0.5f + 32.0f;
                if (v > 320.0f)
                {
                    v = 320.0f;
                }

                lags8[b] = v;
            }

            for (int sf = 0; sf < SubfrCount; sf++)
            {
                cs.BlockLags[sf][0] = lags8[2 * sf];
                cs.BlockLags[sf][1] = lags8[2 * sf + 1];
            }

            var pp = new SmplPitchParams { BlocksegIdx = blocksegIdx };
            for (int sf = 0; sf < 4; sf++)
            {
                pp.GainIdx[sf] = 5;
            }

            Array.Copy(laginds, pp.Laginds, 8);
            return pp;
        }

        private static SmplCandidate VoicedCandidate(SmplSynthTables synthT, double[] win, int winOff, float[] prevNlsf, FrontEndLsf fe, CelpFrameCtx cs, SmplPitchParams pp)
        {
            float[] winN = cs.Es.WinNVoiced;
            for (int i = 0; i < winN.Length; i++)
            {
                winN[i] = (float)(win[winOff + i] / 32768.0);
            }

            var ip = new SmplInternalParams { Pitch = pp, HasPitch = true };
            int bgrid;
            float[] brec = Quantize(fe, synthT, 1, prevNlsf, out bgrid, ip.Lsf.Stage2);
            float[][] predcoefs = SmplLpc.SmplLpcInterpolIdx(brec, fe.PrevLsfq, 0);
            var resLpc = new float[IntfLen];
            for (int sf = 0; sf < SubfrCount; sf++)
            {
                AnalysisResidualSubfr(predcoefs[sf], winN, 0, sf, resLpc, sf * SubfrLen);
            }

            CelpSubframeOut[] celpOut = RunCelpSubframes(cs, predcoefs, resLpc, cs.BlockLags, cs.PercCorrs, SmplPerc.SmplPercEmphV, 1);

            const int main = SmplPerc.SmplCelpIdxMain;
            var pulseVec = new int[IntfLen];
            var fcbg = new int[4];
            for (int sf = 0; sf < SubfrCount; sf++)
            {
                CelpSubframeOut o = celpOut[sf];
                AccumulatePulses(o.Pulses[main], pulseVec, sf);
                int ai = o.AcbIdx[main];
                if (ai < 0)
                {
                    ai = 0;
                }

                if (ai > 15)
                {
                    ai = 15;
                }

                pp.GainIdx[sf] = ai;
                int fi = o.GainIdx[main];
                fcbg[sf] = fi < 0 ? 0 : fi;
            }

            BuildPulseParams(pulseVec, ip.Pulses);
            for (int sf = 0; sf < 4; sf++)
            {
                pp.FiltIdx[sf] = ip.Pulses.Subfr[sf] > 0 ? fcbg[sf] : -1;
            }

            ip.Lsf.Stage1 = 1;
            ip.Lsf.Grid = bgrid;
            ip.Lsf.Extra = 0;
            return new SmplCandidate { Ip = ip, Stage1 = 1, Nlsf = brec };
        }
    }
}
