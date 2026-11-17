// =============================================================================
// SmplPulse
//
// Excitation pulse decode (PVQ-style) for one internal frame: total pulse
// count, recursive split across subframes, per-position run-length magnitudes
// and batched signs.
// Ports: meowcaller mlow/pulse.go (MIT)
// =============================================================================
using System;
using System.Collections.Generic;

namespace Unison.Voip.Engine.Mlow
{
    /// <summary>One uniform raw-symbol write (encode(sym, sym+1, 1&lt;&lt;nbits)).</summary>
    internal struct SmplRawSym
    {
        public uint Sym;
        public uint Nbits;
    }

    /// <summary>Decoded excitation for one internal (20 ms) frame plus the raw symbols for byte-exact replay.</summary>
    internal sealed class SmplPulseResult
    {
        public int[] Pulses;
        public readonly int[] Subfr = new int[4];
        public readonly List<int> MagRuns = new List<int>();
        public readonly List<SmplRawSym> SignSyms = new List<SmplRawSym>();
    }

    internal static class SmplPulse
    {
        /// <summary>Static gain-helper rodata at 0xe8990, indexed by [config*3 + (p4+s1)].</summary>
        private static readonly byte[] PulseCountByte = { 80, 160, 160, 16, 32, 32, 0, 0 };

        /// <summary>Reads the one static rodata table the pulse path needs; every other address reads 0.</summary>
        public static byte Mem8Static(uint addr)
        {
            if (addr >= 0xe8990 && addr < 0xe8998)
            {
                return PulseCountByte[addr - 0xe8990];
            }

            return 0;
        }

        /// <summary>
        /// Decodes the pulse blocks of one internal frame. p2 = frame samples (320),
        /// p3 = num subframes, p4 = regular flag (1), p6 = config (0/1), s1 = LSF stage-1.
        /// </summary>
        public static SmplPulseResult DecodeSmplPulses(RangeDecoder dec, SmplMem mem, int p2, int p3, int p4, int p6, int s1)
        {
            int n = p2 < 0 ? 0 : p2;
            var res = new SmplPulseResult { Pulses = new int[n] };
            CcTables cc = CcTables.Load();

            int idx = p4 + s1;
            int bByte = Mem8Static(unchecked(0xe8990u + (uint)(p6 * 3 + idx)));
            int frameLen4k = bByte * p2 / 320;
            int subfrLen16 = frameLen4k / p3;
            int posPerSubfr = p2 / p3;

            int total;
            if (p6 != 0)
            {
                total = dec.DecodeCdf(cc.NPulseCount(idx));
            }
            else
            {
                uint l = unchecked((uint)frameLen4k);
                uint ft = TriT(l, l);
                if (ft == 0)
                {
                    ft = 1;
                }

                uint val = dec.Decode(ft);
                uint limit = unchecked((uint)frameLen4k + 1);
                uint prevCum = 0;
                uint k = 0;
                while (k != limit)
                {
                    uint cum = TriT(k, l);
                    if (prevCum <= val && val < cum)
                    {
                        dec.Update(prevCum, cum, ft);
                        break;
                    }

                    prevCum = cum;
                    k++;
                }

                total = unchecked((int)k);
            }

            int[] split;
            if (!DecodePulseSubframeCounts(dec, cc, total, subfrLen16, p3, out split))
            {
                return res;
            }

            int take = p3 < 0 ? 0 : (p3 > 4 ? 4 : p3);
            Array.Copy(split, res.Subfr, take);

            int posPer = posPerSubfr;
            var posList = new List<int>();
            var magList = new List<int>();
            int pulseIdx = -1;
            for (int subfr = 0; subfr < p3; subfr++)
            {
                int cnt = split[subfr];
                if (cnt <= 0)
                {
                    continue;
                }

                int basePos = posPer * subfr;
                int runPos = basePos;
                int pos = posPer;
                int c = cnt;
                int kk = 0;
                while (kk < cnt)
                {
                    if (pos < 0)
                    {
                        break;
                    }

                    int oct = (pos + 7) / 8;
                    RunlenCmfs bucket = cc.Runlen(oct);
                    int start = bucket.MaxSamples - pos;
                    int m = dec.DecodeCdf(CcTables.CdfWindow(bucket.Cmf(c), start, pos + 1));
                    res.MagRuns.Add(m);
                    if (m > 0 || kk == 0)
                    {
                        pulseIdx++;
                        runPos += m;
                        posList.Add(runPos);
                        magList.Add(1);
                        pos -= m;
                    }
                    else if (pulseIdx >= 0)
                    {
                        magList[pulseIdx]++;
                    }

                    c--;
                    kk++;
                }
            }

            int numPos = pulseIdx + 1;

            if (numPos > 0)
            {
                int p = 0;
                while (p <= pulseIdx)
                {
                    int nbits = numPos - p;
                    if (nbits >= 15)
                    {
                        nbits = 15;
                    }

                    if (nbits <= 0)
                    {
                        break;
                    }

                    uint sym = dec.DecodeRawSymbol((uint)nbits);
                    res.SignSyms.Add(new SmplRawSym { Sym = sym, Nbits = (uint)nbits });
                    uint bitfield = sym << (16 - nbits);
                    int end = p + nbits;
                    for (int q = p; q < end; q++)
                    {
                        int sign = (int)((bitfield >> 14) & 2) - 1;
                        magList[q] *= sign;
                        bitfield <<= 1;
                    }

                    p = end;
                }
            }

            for (int i = 0; i < numPos; i++)
            {
                int pp = posList[i];
                if (pp >= 0 && pp < res.Pulses.Length)
                {
                    res.Pulses[pp] = magList[i];
                }
            }

            return res;
        }

        /// <summary>Cumulative triangular prior over [0, l] (16-bit wrapped).</summary>
        private static uint TriT(uint k, uint l)
        {
            uint a = unchecked((k + 2) * (l + 1));
            uint b = unchecked(((k - 1) * (k + 131070)) >> 1);
            return unchecked(a - b) & 0xffff;
        }

        /// <summary>Splits total pulses across subframes; split has 8 slots (only the first p3 are used).</summary>
        internal static bool DecodePulseSubframeCounts(RangeDecoder dec, CcTables cc, int total, int subfrLen16, int p3, out int[] split)
        {
            split = new int[8];
            if (total == 0)
            {
                return true;
            }

            if (p3 == 2)
            {
                int s0 = SmplSplit3537(dec, cc, total, subfrLen16);
                if (s0 < 0)
                {
                    return false;
                }

                split[0] = s0;
                split[1] = total - s0;
                return true;
            }

            int sum = total - subfrLen16 * 2;
            if (sum < 0)
            {
                sum = 0;
            }

            int lo = total - 80;
            if (lo < 0)
            {
                lo = 0;
            }

            if (sum < lo)
            {
                return false;
            }

            int hiBound = total - lo;
            if (sum < hiBound)
            {
                sum += dec.DecodeCdf(CcTables.CdfWindow(cc.SplitCmf(total), sum - lo, (hiBound - sum) + 2));
            }

            if (sum > 0)
            {
                int s0 = SmplSplit3537(dec, cc, sum, subfrLen16);
                split[0] = s0;
                split[1] = sum - s0;
            }

            if (sum < total)
            {
                int s2 = SmplSplit3537(dec, cc, total - sum, subfrLen16);
                split[2] = s2;
                split[3] = (total - sum) - s2;
            }

            if (split[0] == -1 || split[2] == -1)
            {
                Array.Clear(split, 0, split.Length);
            }

            return true;
        }

        /// <summary>func 3537: count assigned to the first half of a range.</summary>
        private static int SmplSplit3537(RangeDecoder dec, CcTables cc, int count, int granularity)
        {
            int lo = count;
            if (granularity < lo)
            {
                lo = granularity;
            }

            int minSplit = count - granularity;
            if (minSplit < 0)
            {
                minSplit = 0;
            }

            if (lo < minSplit)
            {
                return -1;
            }

            if (minSplit == lo)
            {
                return minSplit;
            }

            int n = (lo - minSplit) + 2;
            return dec.DecodeCdf(CcTables.CdfWindow(cc.SplitCmf(count), minSplit, n)) + minSplit;
        }
    }
}
