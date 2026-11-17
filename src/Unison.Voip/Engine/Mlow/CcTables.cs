// =============================================================================
// CcTables
//
// Seed-built nrgres/gains (Group A/E), LTP gain (Group C) and pulse (Group B)
// CDF tables: integer DCMF expansion of cc_seed.bin, split/run-length pulse CDFs
// from the SILK fixed-point model, and the gain-reconstruction rodata verbatim.
// Ports: meowcaller mlow/cc_tables.go (MIT)
// =============================================================================
using System;
using System.Collections.Generic;

namespace Unison.Voip.Engine.Mlow
{
    internal sealed class RunlenCmfs
    {
        private readonly ushort[][] _cmfs;

        public RunlenCmfs(int maxSamples, ushort[][] cmfs)
        {
            MaxSamples = maxSamples;
            _cmfs = cmfs;
        }

        public int MaxSamples { get; }

        public ushort[] Cmf(int c)
        {
            return _cmfs[c - 1];
        }
    }

    internal sealed class CcTables
    {
        private const int MaxPulsesPerSf = 40;
        private const int RunlengthStep = 8;
        private const int NumRunlenCmfs = 20;
        private const int SplitNumTables = MaxPulsesPerSf * 4 - 1;
        private const int FcbgOffsetSteps = 176;
        private const int FcbgOffsetBuckets = 4;
        private const int AcbgN = 16;
        private const int FcbgVN = 34;

        private const int Log2Exp1Q15 = 47274;
        private const int Log22PiQ14 = 43442;
        private const long OneQ31 = 1L << 31;

        private static readonly byte[] NrgresGain2Dcmf =
        {
            139, 194, 196, 200, 209, 225, 239, 243, 246, 246, 253, 255,
            253, 249, 244, 239, 233, 231, 230, 228, 219, 216, 214, 214,
            213, 211, 214, 215, 220, 223, 230, 234, 237, 240, 242, 244,
            245, 245, 246, 242, 237, 230, 220, 210, 190, 170, 148, 125,
            104, 83, 64, 49, 36, 22, 14, 8, 8, 8, 8, 8, 8, 8, 8, 8,
            8, 8, 8, 8, 8, 8, 8,
        };

        private static readonly byte[] NrgresShape2Dcmf =
        {
            47, 52, 137, 58, 61, 21, 73, 240, 55, 95, 255,
            68, 83, 108, 48, 50, 49, 45, 35, 155, 31, 41,
        };

        private static readonly Lazy<CcTables> Instance = new Lazy<CcTables>(Build);

        private ushort[] _nrgresGain2;
        private ushort[] _nrgresShape2;
        private ushort[] _nrgresGain4;
        private ushort[] _nrgresShape4;
        private ushort[][] _fcbgOffset;
        private ushort[] _acbgainsHr;
        private ushort[] _acbgainsLr;
        private ushort[] _fcbgainsV;
        private ushort[] _fcbgainsVDelta;
        private short[] _acbgainsCbHrQ14;
        private short[] _acbgainsCbLrQ14;
        private short[] _gainRecon;
        private uint _gainReconBase;
        private ushort[][] _nPulseCmfs;
        private ushort[][] _splitCmfs;
        private RunlenCmfs[] _runlen;

        private CcTables()
        {
        }

        public static CcTables Load()
        {
            return Instance.Value;
        }

        // --- SILK fixed-point primitives ---

        internal static int CcSmulbb(int a, int b)
        {
            return unchecked((short)a) * unchecked((short)b);
        }

        internal static int CcSmlawb(int a, int b, int c)
        {
            return unchecked((int)(a + (((long)b * unchecked((short)c)) >> 16)));
        }

        internal static void CcClzFrac(int input, out int lz, out int fracQ7)
        {
            uint u = unchecked((uint)input);
            lz = 32 - RangeCoderConst.Ilog(u);
            int k = (24 - lz) & 31;
            uint rot = k == 0 ? u : (u >> k) | (u << (32 - k));
            fracQ7 = unchecked((int)rot) & 0x7f;
        }

        internal static int CcLin2Log(int inLin)
        {
            int lz, fracQ7;
            CcClzFrac(inLin, out lz, out fracQ7);
            return CcSmlawb(fracQ7, fracQ7 * (128 - fracQ7), 179) + ((31 - lz) << 7);
        }

        internal static int CcLog2Lin(int inLogQ7)
        {
            if (inLogQ7 < 0)
            {
                return 0;
            }

            if (inLogQ7 >= 3967)
            {
                return 0x7fffffff;
            }

            int o = 1 << (inLogQ7 >> 7);
            int fracQ7 = inLogQ7 & 0x7f;
            int inner = CcSmlawb(fracQ7, CcSmulbb(fracQ7, 128 - fracQ7), -174);
            if (inLogQ7 < 2048)
            {
                o = unchecked(o + ((o * inner) >> 7));
            }
            else
            {
                o = unchecked(o + (o >> 7) * inner);
            }

            return o;
        }

        internal static int CcSigmQ15(int inQ5)
        {
            int[] slope = { 237, 153, 73, 30, 12, 7 };
            int[] pos = { 16384, 23955, 28861, 31213, 32178, 32548 };
            int[] neg = { 16384, 8812, 3906, 1554, 589, 219 };
            if (inQ5 < 0)
            {
                int v = -inQ5;
                if (v >= 6 * 32)
                {
                    return 0;
                }

                int ind = v >> 5;
                return neg[ind] - CcSmulbb(slope[ind], v & 0x1f);
            }

            if (inQ5 >= 6 * 32)
            {
                return 32767;
            }

            int i = inQ5 >> 5;
            return pos[i] + CcSmulbb(slope[i], inQ5 & 0x1f);
        }

        // --- pulse-coding table builders ---

        /// <summary>smpl_pdf_to_CMF (maxval==-1 path): truncating-int normalize into a u16 CDF.</summary>
        internal static ushort[] PdfToCmf(int[] pdf)
        {
            long n = pdf.Length;
            const long maxval = 32767;
            long sump = 0;
            foreach (int x in pdf)
            {
                sump += x;
            }

            var cmf = new ushort[pdf.Length + 1];
            for (int i = 0; i < pdf.Length; i++)
            {
                long p = (pdf[i] * (maxval - n)) / sump + 1;
                cmf[i + 1] = unchecked((ushort)(int)(cmf[i] + (int)p));
            }

            return cmf;
        }

        internal static int CcStirling(int n)
        {
            if (n == 0)
            {
                return 0;
            }

            int ret = unchecked(((n << 1) + 1) * (CcLin2Log(n) << 7));
            ret = unchecked(ret - Log2Exp1Q15 * n);
            ret = unchecked(ret + Log22PiQ14);
            return unchecked(ret + Log2Exp1Q15 / (12 * n));
        }

        internal static int CcProbSplitFast(int k, int n)
        {
            int tmp = unchecked(CcStirling(n) - CcStirling(k) - CcStirling(n - k) - n * (1 << 15));
            if (tmp == 0)
            {
                return 1 << 30;
            }

            int ret = CcLog2Lin((-tmp) >> 8);
            return (1 << 30) / ret;
        }

        private static ushort[][] CreateSplitCmfs()
        {
            var o = new ushort[SplitNumTables][];
            for (int numPulses = 1; numPulses <= SplitNumTables; numPulses++)
            {
                int minSplit = numPulses - MaxPulsesPerSf * 2;
                if (minSplit < 0)
                {
                    minSplit = 0;
                }

                int maxSplit = numPulses - minSplit;
                var p = new int[maxSplit - minSplit + 1];
                for (int k = minSplit; k <= maxSplit; k++)
                {
                    p[k - minSplit] = CcProbSplitFast(k, numPulses);
                }

                o[numPulses - 1] = PdfToCmf(p);
            }

            return o;
        }

        private static RunlenCmfs CreateRunlenTable(int maxSamples)
        {
            int ms = maxSamples;
            var cmfs = new ushort[MaxPulsesPerSf][];
            for (int nump = 1; nump <= MaxPulsesPerSf; nump++)
            {
                long plongerQ31 = OneQ31;
                var p = new int[ms];
                for (int nums = 1; nums <= ms; nums++)
                {
                    long tmp = OneQ31 - (OneQ31 / (ms - nums + 1));
                    long p1Q31 = tmp;
                    for (int r = 0; r < nump - 1; r++)
                    {
                        p1Q31 = (p1Q31 * tmp) >> 31;
                    }

                    p1Q31 = OneQ31 - p1Q31;
                    if (p1Q31 > 2147376274)
                    {
                        p1Q31 = 2147376274;
                    }

                    int logOutQ7;
                    if (nump > ms)
                    {
                        logOutQ7 = CcLin2Log((nump << 10) / ms) - 10 * 128;
                    }
                    else
                    {
                        logOutQ7 = -(CcLin2Log((ms << 10) / nump) - 10 * 128);
                    }

                    const int sigmBiasQ5 = 146;
                    const int scaleMaxQ15 = 36000;
                    const int scaleMinQ15 = 26000;
                    int scaleFacQ15 = scaleMaxQ15 - (((scaleMaxQ15 - scaleMinQ15) * CcSigmQ15((logOutQ7 >> 2) + sigmBiasQ5)) >> 15);
                    p1Q31 = OneQ31 - CcLog2Lin(((scaleFacQ15 * (CcLin2Log(unchecked((int)(OneQ31 - p1Q31))) - 31 * 128)) >> 15) + 31 * 128);
                    if (p1Q31 > 2147376274)
                    {
                        p1Q31 = 2147376274;
                    }

                    p[nums - 1] = unchecked((int)((plongerQ31 * p1Q31) >> 31));
                    plongerQ31 = (plongerQ31 * (OneQ31 - p1Q31)) >> 31;
                }

                cmfs[nump - 1] = PdfToCmf(p);
            }

            return new RunlenCmfs(ms, cmfs);
        }

        // --- seed parse + table build ---

        /// <summary>Integer dcmf-&gt;cmf returning a u16 CDF of length len+1.</summary>
        internal static ushort[] CcDcmf(byte[] dcmf)
        {
            return CcDcmf(dcmf, 0, dcmf.Length);
        }

        internal static ushort[] CcDcmf(byte[] dcmf, int offset, int count)
        {
            var c = new ushort[count + 1];
            CelpTables.CelpDcmfToCmf(dcmf, offset, count, c);
            return c;
        }

        private static ushort[][] CcDcmfChunks(byte[] b, int step)
        {
            var o = new List<ushort[]>(b.Length / step);
            for (int i = 0; i + step <= b.Length; i += step)
            {
                o.Add(CcDcmf(b, i, step));
            }

            return o.ToArray();
        }

        private static CcTables Build()
        {
            Dictionary<int, ProtoField> f = SeedProto.ParseProto(SeedProto.LoadInflated("cc_seed.bin"));
            byte[] acbgainsHrDcmf = SeedProto.BytesOf(f, 4);
            byte[] gainRecon = SeedProto.BytesOf(f, 9);

            var t = new CcTables
            {
                _nrgresGain2 = CcDcmf(NrgresGain2Dcmf),
                _nrgresShape2 = CcDcmf(NrgresShape2Dcmf),
                _nrgresGain4 = CcDcmf(SeedProto.BytesOf(f, 1)),
                _nrgresShape4 = CcDcmf(SeedProto.BytesOf(f, 2)),
                _fcbgOffset = CcDcmfChunks(SeedProto.BytesOf(f, 3), FcbgOffsetSteps),
                _fcbgainsV = CcDcmf(SeedProto.BytesOf(f, 5)),
                _fcbgainsVDelta = CcDcmf(SeedProto.BytesOf(f, 6)),
                _gainReconBase = unchecked((uint)SeedProto.VarintOf(f, 8)),
            };

            var hr = new List<ushort>();
            for (int i = 0; i + AcbgN <= acbgainsHrDcmf.Length; i += AcbgN)
            {
                hr.AddRange(CcDcmf(acbgainsHrDcmf, i, AcbgN));
            }

            t._acbgainsHr = hr.ToArray();

            var lr = new List<ushort>();
            byte[] lrDcmf = CelpTables.CelpAcbgainsDcmfLR;
            for (int i = 0; i + AcbgN <= lrDcmf.Length; i += AcbgN)
            {
                lr.AddRange(CcDcmf(lrDcmf, i, AcbgN));
            }

            t._acbgainsLr = lr.ToArray();

            int[] cbHr = SeedProto.DecodeZigzagVarints(SeedProto.BytesOf(f, 7));
            t._acbgainsCbHrQ14 = new short[cbHr.Length];
            for (int i = 0; i < cbHr.Length; i++)
            {
                t._acbgainsCbHrQ14[i] = unchecked((short)cbHr[i]);
            }

            t._acbgainsCbLrQ14 = CelpTables.CbAcbgainsLRQ14;

            var gr = new List<short>();
            for (int i = 0; i + 1 < gainRecon.Length; i += 2)
            {
                gr.Add(unchecked((short)(ushort)(gainRecon[i] | gainRecon[i + 1] << 8)));
            }

            t._gainRecon = gr.ToArray();
            t._nPulseCmfs = new[]
            {
                CcDcmf(SeedProto.BytesOf(f, 10)),
                CcDcmf(SeedProto.BytesOf(f, 11)),
                CcDcmf(SeedProto.BytesOf(f, 12)),
            };
            t._splitCmfs = CreateSplitCmfs();
            t._runlen = new RunlenCmfs[NumRunlenCmfs];
            for (int oct = 1; oct <= NumRunlenCmfs; oct++)
            {
                t._runlen[oct - 1] = CreateRunlenTable(oct * RunlengthStep);
            }

            return t;
        }

        // --- accessors (logical-index API matching smpl_cc_tables.rs) ---

        public ushort[] NrgresGain(int numSubframes)
        {
            return numSubframes == 2 ? _nrgresGain2 : _nrgresGain4;
        }

        public ushort[] NrgresShape(int numSubframes)
        {
            return numSubframes == 2 ? _nrgresShape2 : _nrgresShape4;
        }

        public ushort[] NrgresGain4()
        {
            return NrgresGain(4);
        }

        public ushort[] NrgresShape4()
        {
            return NrgresShape(4);
        }

        public ArraySegment<ushort> FcbgOffset(int tableIx, int bucket, int minOffset)
        {
            return new ArraySegment<ushort>(_fcbgOffset[tableIx * FcbgOffsetBuckets + bucket], minOffset, 92);
        }

        public ArraySegment<ushort> AcbgainRow(int prev)
        {
            return new ArraySegment<ushort>(_acbgainsHr, (prev + 1) * (AcbgN + 1), AcbgN + 1);
        }

        public ArraySegment<ushort> AcbgainRowLr(int prev)
        {
            return new ArraySegment<ushort>(_acbgainsLr, (prev + 1) * (AcbgN + 1), AcbgN + 1);
        }

        public void AcbgainWeights(int gi, out int w0, out int w1)
        {
            w0 = _acbgainsCbHrQ14[gi * 2];
            w1 = _acbgainsCbHrQ14[gi * 2 + 1];
        }

        public void AcbgainWeightsLr(int gi, out int w0, out int w1)
        {
            w0 = _acbgainsCbLrQ14[gi * 2];
            w1 = _acbgainsCbLrQ14[gi * 2 + 1];
        }

        public ushort[] FcbgainV()
        {
            return _fcbgainsV;
        }

        public ArraySegment<ushort> FcbgainVDelta(int prevFilt)
        {
            return new ArraySegment<ushort>(_fcbgainsVDelta, FcbgVN - 1 - prevFilt, 35);
        }

        private int GainReconAt(uint addr)
        {
            long off = (long)addr - _gainReconBase;
            if (off >= 0 && off % 2 == 0 && off / 2 < _gainRecon.Length)
            {
                return _gainRecon[off / 2];
            }

            return 0;
        }

        public int NrgStep(int cfg)
        {
            return GainReconAt(unchecked(_gainReconBase + (uint)cfg * 2));
        }

        public int GainRecon(bool p4, int idx)
        {
            uint b = p4 ? 0xf35f0u : 0xf3970u;
            return GainReconAt(unchecked(b + (uint)idx * 2));
        }

        public ushort[] NPulseCount(int idx)
        {
            return _nPulseCmfs[idx];
        }

        public ushort[] SplitCmf(int total)
        {
            int i = total - 1;
            if (i < 0 || i >= _splitCmfs.Length)
            {
                return null;
            }

            return _splitCmfs[i];
        }

        public RunlenCmfs Runlen(int oct)
        {
            return _runlen[oct - 1];
        }

        /// <summary>The n-entry CDF window base[start:start+n], zero-filling out-of-range entries.</summary>
        internal static ushort[] CdfWindow(ushort[] b, int start, int n)
        {
            var w = new ushort[n];
            int len = b == null ? 0 : b.Length;
            for (int i = 0; i < n; i++)
            {
                int j = start + i;
                if (j >= 0 && j < len)
                {
                    w[i] = b[j];
                }
            }

            return w;
        }
    }
}
