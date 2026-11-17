// =============================================================================
// SmplVad
//
// SILK fixed-point VAD: per-internal-frame speech-activity probability and the
// packet coded_as_active_voice flag (2-band allpass filterbank, noise-level
// tracking, SNR -> sigmoid, per-packet DTX hangover) on 16 kHz int16 PCM.
// Ports: meowcaller mlow/vad.go (MIT)
// =============================================================================
namespace Unison.Voip.Engine.Mlow
{
    /// <summary>VAD output for one 60 ms packet.</summary>
    internal sealed class VadPacketResult
    {
        public readonly float[] VadResults = new float[3];
        public bool CodedAsActiveVoice;
    }

    /// <summary>Persistent SILK VAD state, carried across packets.</summary>
    internal sealed class SmplVadState
    {
        private const int SilkInt32Max = 0x7FFFFFFF;
        private const int SilkInt16Max = 32767;
        private const int SilkInt16Min = -0x8000;
        private const int SilkUint8Max = 0xFF;

        private const int NBands = 4;
        private const int InternalSubframesLog2 = 2;
        private const int InternalSubframes = 1 << InternalSubframesLog2;
        private const int NoiseLevelSmoothCoefQ16 = 1024;
        private const int NoiseLevelsBias = 50;
        private const int NegativeOffsetQ5 = 128;
        private const int SnrFactorQ16 = 45000;
        private const int AFB120 = 3894 << 1;
        private const int AFB121 = -29322;
        private const int SpeechActivityDtxThresQ8 = 12;

        private const int VadActive = 0;
        private const int VadInactive = 1;
        private const int VadHangover = 2;

        private static readonly int[] TiltWeights = { 30000, 6000, -12000, -12000 };
        private static readonly int[] SigmSlope = { 237, 153, 73, 30, 12, 7 };
        private static readonly int[] SigmPos = { 16384, 23955, 28861, 31213, 32178, 32548 };
        private static readonly int[] SigmNeg = { 16384, 8812, 3906, 1554, 589, 219 };

        private readonly int[] _anaState = new int[2];
        private readonly int[] _anaState1 = new int[2];
        private readonly int[] _anaState2 = new int[2];
        private readonly int[] _xnrgSubfr = new int[NBands];
        private readonly int[] _nl = new int[NBands];
        private readonly int[] _invNl = new int[NBands];
        private readonly int[] _noiseLevelBias = new int[NBands];
        private int _counter;
        private int _hpState;
        private int _noiseLvlUpdateSpeed;
        private int _nonBinariness;
        private int _highpassSharpness;
        private int _remainingDtxHangover;
        private int _hangoverMs;

        private int[] _frame = new int[0];
        private int[] _x = new int[0];
        private readonly int[] _xnrg = new int[NBands];
        private readonly int[] _vt = new int[3];

        public SmplVadState()
        {
            _counter = 15;
            _remainingDtxHangover = 60;
            _hangoverMs = 60;
            for (int b = 0; b < NBands; b++)
            {
                int bias = NoiseLevelsBias / (b + 1);
                if (bias < 1)
                {
                    bias = 1;
                }

                _noiseLevelBias[b] = bias;
                _nl[b] = 100 * bias;
                _invNl[b] = SilkInt32Max / _nl[b];
            }
        }

        private static int Sat16(int a)
        {
            if (a < SilkInt16Min)
            {
                return SilkInt16Min;
            }

            return a > SilkInt16Max ? SilkInt16Max : a;
        }

        private static int Smulwb(int a, int b)
        {
            return unchecked((int)(((long)a * (short)b) >> 16));
        }

        private static int Smlawb(int a, int b, int c)
        {
            return unchecked((int)((long)a + (((long)b * (short)c) >> 16)));
        }

        private static int Smulww(int a, int b)
        {
            return unchecked((int)(((long)a * b) >> 16));
        }

        private static int Smulbb(int a, int b)
        {
            return unchecked((short)a * (short)b);
        }

        private static int Smlabb(int a, int b, int c)
        {
            return unchecked(a + (short)b * (short)c);
        }

        private static int AddPosSat32(int a, int b)
        {
            uint s = unchecked((uint)a + (uint)b);
            if ((s & 0x80000000u) != 0)
            {
                return SilkInt32Max;
            }

            return unchecked((int)s);
        }

        private static int Clz32(int x)
        {
            uint v = unchecked((uint)x);
            if (v == 0)
            {
                return 32;
            }

            int n = 0;
            while ((v & 0x80000000u) == 0)
            {
                v <<= 1;
                n++;
            }

            return n;
        }

        private static int Ror32(int a32, int rot)
        {
            uint x = unchecked((uint)a32);
            int r = rot & 31;
            return unchecked((int)((x >> r) | (x << ((32 - r) & 31))));
        }

        private static void ClzFrac(int inp, out int lz, out int fracQ7)
        {
            lz = Clz32(inp);
            fracQ7 = Ror32(inp, 24 - lz) & 0x7f;
        }

        private static int Lin2Log(int inLin)
        {
            int lz, fracQ7;
            ClzFrac(inLin, out lz, out fracQ7);
            return Smlawb(fracQ7, fracQ7 * (128 - fracQ7), 179) + ((31 - lz) << 7);
        }

        private static int SqrtApprox(int x)
        {
            if (x <= 0)
            {
                return 0;
            }

            int lz, fracQ7;
            ClzFrac(x, out lz, out fracQ7);
            int y = (lz & 1) != 0 ? 32768 : 46214;
            y >>= lz >> 1;
            return Smlawb(y, y, Smulbb(213, fracQ7));
        }

        private static int SigmQ15(int inQ5)
        {
            if (inQ5 < 0)
            {
                inQ5 = -inQ5;
                if (inQ5 >= 6 * 32)
                {
                    return 0;
                }

                int ind = inQ5 >> 5;
                return SigmNeg[ind] - Smulbb(SigmSlope[ind], inQ5 & 0x1F);
            }

            if (inQ5 >= 6 * 32)
            {
                return 32767;
            }

            int i = inQ5 >> 5;
            return SigmPos[i] + Smulbb(SigmSlope[i], inQ5 & 0x1F);
        }

        private static int RshiftRound(int a, int shift)
        {
            if (shift == 1)
            {
                return (a >> 1) + (a & 1);
            }

            return ((a >> (shift - 1)) + 1) >> 1;
        }

        private void FiltHP(int[] x, int bQ16, int aNegQ16, int length)
        {
            for (int i = 0; i < length; i++)
            {
                int inval = Smulwb(bQ16, x[i]);
                int outval = Sat16(unchecked(_hpState - inval));
                _hpState = Smlawb(inval, aNegQ16, outval);
                x[i] = outval;
            }
        }

        private static void AnaFiltBank1(int[] inp, int[] s, int[] o, int lOff, int hOff, int n)
        {
            unchecked
            {
                int n2 = n >> 1;
                for (int k = 0; k < n2; k++)
                {
                    int in32 = inp[2 * k] << 10;
                    int y = in32 - s[0];
                    int x = Smlawb(y, y, AFB121);
                    int out1 = s[0] + x;
                    s[0] = in32 + x;

                    in32 = inp[2 * k + 1] << 10;
                    y = in32 - s[1];
                    x = Smulwb(y, AFB120);
                    int out2 = s[1] + x;
                    s[1] = in32 + x;

                    o[lOff + k] = Sat16(RshiftRound(out2 + out1, 11));
                    o[hOff + k] = Sat16(RshiftRound(out2 - out1, 11));
                }
            }
        }

        private static void AnaFiltBank1Inplace(int[] x, int hiOff, int[] s, int n)
        {
            unchecked
            {
                int n2 = n >> 1;
                for (int k = 0; k < n2; k++)
                {
                    int in32 = x[2 * k] << 10;
                    int y = in32 - s[0];
                    int xx = Smlawb(y, y, AFB121);
                    int out1 = s[0] + xx;
                    s[0] = in32 + xx;

                    in32 = x[2 * k + 1] << 10;
                    y = in32 - s[1];
                    xx = Smulwb(y, AFB120);
                    int out2 = s[1] + xx;
                    s[1] = in32 + xx;

                    x[hiOff + k] = Sat16(RshiftRound(out2 - out1, 11));
                    x[k] = Sat16(RshiftRound(out2 + out1, 11));
                }
            }
        }

        private void GetNoiseLevels(int[] pX)
        {
            int minCoef = 0;
            if (_counter < 1000)
            {
                minCoef = SilkInt16Max / ((_counter >> 4) + 1);
                _counter++;
            }

            for (int b = 0; b < NBands; b++)
            {
                int nl = _nl[b];
                int nrg = AddPosSat32(pX[b], _noiseLevelBias[b]);
                int invNrg = SilkInt32Max / nrg;
                int coef;
                if (nrg > unchecked(nl << 3))
                {
                    coef = NoiseLevelSmoothCoefQ16 >> 3;
                }
                else if (nrg < nl)
                {
                    coef = NoiseLevelSmoothCoefQ16;
                }
                else
                {
                    coef = Smulwb(Smulww(invNrg, nl), NoiseLevelSmoothCoefQ16 << 1);
                }

                coef = unchecked(coef * (100 + _noiseLvlUpdateSpeed)) / 100;
                if (coef < minCoef)
                {
                    coef = minCoef;
                }

                _invNl[b] = Smlawb(_invNl[b], unchecked(invNrg - _invNl[b]), coef);
                int v = SilkInt32Max / _invNl[b];
                if (v > 0x00FFFFFF)
                {
                    v = 0x00FFFFFF;
                }

                _nl[b] = v;
            }
        }

        private int GetSAQ8(int[] pIn, int framelen)
        {
            int decFl1 = framelen >> 1;
            int decFl2 = framelen >> 2;
            int decFl3 = framelen >> 3;

            int x1 = decFl3 + decFl2;
            int x2 = x1 + decFl3;
            int x3 = x2 + decFl2;
            int xTotal = x3 + decFl1;
            if (_x.Length != xTotal)
            {
                _x = new int[xTotal];
            }

            int[] x = _x;
            int[] xOffset = { 0, x1, x2, x3 };

            AnaFiltBank1(pIn, _anaState, x, 0, x3, framelen);
            AnaFiltBank1Inplace(x, x2, _anaState1, decFl1);
            AnaFiltBank1Inplace(x, x1, _anaState2, decFl2);

            int aNegQ16 = 53084;
            aNegQ16 = (aNegQ16 * (100 - _highpassSharpness)) / 100;
            int bQ16 = (65536 + aNegQ16) / 2;
            FiltHP(x, bQ16, aNegQ16, decFl3);

            int[] xnrg = _xnrg;
            for (int b = 0; b < NBands; b++)
            {
                int shift = NBands - b;
                if (shift > NBands - 1)
                {
                    shift = NBands - 1;
                }

                int dec = framelen >> shift;
                int decSubfrLen = dec >> InternalSubframesLog2;
                int decSubfrOffset = 0;
                xnrg[b] = _xnrgSubfr[b];
                int sumSquared = 0;
                for (int sub = 0; sub < InternalSubframes; sub++)
                {
                    sumSquared = 0;
                    for (int i = 0; i < decSubfrLen; i++)
                    {
                        int xTmp = x[xOffset[b] + i + decSubfrOffset] >> 3;
                        sumSquared = Smlabb(sumSquared, xTmp, xTmp);
                    }

                    xnrg[b] = sub < InternalSubframes - 1
                        ? AddPosSat32(xnrg[b], sumSquared)
                        : AddPosSat32(xnrg[b], sumSquared >> 1);
                    decSubfrOffset += decSubfrLen;
                }

                _xnrgSubfr[b] = sumSquared;
            }

            GetNoiseLevels(xnrg);

            int sumSq = 0;
            int inputTilt = 0;
            for (int b = 0; b < NBands; b++)
            {
                int speechNrg = unchecked(xnrg[b] - _nl[b]);
                if (speechNrg > 0)
                {
                    int ratioQ8;
                    if ((xnrg[b] & -0x00800000) == 0)
                    {
                        ratioQ8 = (xnrg[b] << 8) / (_nl[b] + 1);
                    }
                    else
                    {
                        ratioQ8 = xnrg[b] / ((_nl[b] >> 8) + 1);
                    }

                    int snrQ7 = Lin2Log(ratioQ8) - 8 * 128;
                    sumSq = Smlabb(sumSq, snrQ7, snrQ7);
                    if (speechNrg < (1 << 20))
                    {
                        snrQ7 = Smulwb(SqrtApprox(speechNrg) << 6, snrQ7);
                    }

                    inputTilt = Smlawb(inputTilt, TiltWeights[b], snrQ7);
                }
            }

            sumSq = sumSq / NBands;
            int pSnrDbQ7 = unchecked((short)(3 * SqrtApprox(sumSq)));

            int snrFactorQ16 = (SnrFactorQ16 * (150 - _nonBinariness)) / 150;
            int saQ15 = SigmQ15(Smulwb(snrFactorQ16, pSnrDbQ7) - NegativeOffsetQ5);

            int r = saQ15 >> 7;
            return r > SilkUint8Max ? SilkUint8Max : r;
        }

        /// <summary>Processes one 60 ms packet (3 internal frames of framelen int16 samples).</summary>
        public VadPacketResult ProcessPacket(short[] pcmI16, int framelen)
        {
            const int framesPerPacket = 3;
            const int packetMs = 60;
            var res = new VadPacketResult();
            if (pcmI16.Length < framesPerPacket * framelen)
            {
                return res;
            }

            if (_frame.Length != framelen)
            {
                _frame = new int[framelen];
            }

            int[] vt = _vt;
            for (int i = 0; i < framesPerPacket; i++)
            {
                int t = i * framelen;
                for (int j = 0; j < framelen; j++)
                {
                    _frame[j] = pcmI16[t + j];
                }

                int saQ8 = GetSAQ8(_frame, framelen);
                res.VadResults[i] = (float)saQ8 / 256.0f;
                vt[i] = saQ8 > SpeechActivityDtxThresQ8 ? VadActive : VadInactive;
            }

            for (int i = 0; i < framesPerPacket; i++)
            {
                if (vt[i] == VadActive)
                {
                    _remainingDtxHangover = _hangoverMs;
                }
                else if (_remainingDtxHangover > 0)
                {
                    vt[i] = VadHangover;
                    _remainingDtxHangover -= packetMs / framesPerPacket;
                }

                if (vt[i] != VadInactive)
                {
                    res.CodedAsActiveVoice = true;
                }
            }

            return res;
        }
    }
}
