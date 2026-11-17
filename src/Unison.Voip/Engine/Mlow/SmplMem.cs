// =============================================================================
// SmplMem
//
// Embedded window of the codec's heap holding the pitch lag/contour (Group D)
// tables at their fixed WASM addresses, so the pitch decode can replicate the
// original pointer arithmetic exactly. Reads outside every region return 0.
// Ports: meowcaller mlow/mem.go (MIT)
// =============================================================================
using System;
using System.Collections.Generic;

namespace Unison.Voip.Engine.Mlow
{
    internal sealed class SmplMem
    {
        public const uint MemGClk = 0xb9f9a8;
        public const uint MemGPitch = 0xb9d378;
        public const uint MemPcfg = MemGClk + 0x5704;
        public const uint MemHdrContourMap = 0xe7c10;
        public const uint MemHdrLagCdf = 0xbaa7b0;
        public const uint MemHdrFracBase = 0xbaa9be;
        public const uint MemHdrDeltaCdf = 0xbab13e;
        public const uint MemDeltaBounds = 0xe7ef0;
        public const uint MemNumContours = 217;

        private static readonly uint[] MemHdrUnused = { 0xe7d20, 0xe7ef0, 0xe8096 };

        /// <summary>Q12 cosine approximation table (129 entries, symmetric around index 64).</summary>
        public static readonly int[] SilkLsfCosTabFixQ12 =
        {
            8192, 8190, 8182, 8170, 8152, 8130, 8104, 8072,
            8034, 7994, 7946, 7896, 7840, 7778, 7714, 7644,
            7568, 7490, 7406, 7318, 7226, 7128, 7026, 6922,
            6812, 6698, 6580, 6458, 6332, 6204, 6070, 5934,
            5792, 5648, 5502, 5352, 5198, 5040, 4880, 4718,
            4552, 4382, 4212, 4038, 3862, 3684, 3502, 3320,
            3136, 2948, 2760, 2570, 2378, 2186, 1990, 1794,
            1598, 1400, 1202, 1002, 802, 602, 402, 202,
            0, -202, -402, -602, -802, -1002, -1202, -1400,
            -1598, -1794, -1990, -2186, -2378, -2570, -2760, -2948,
            -3136, -3320, -3502, -3684, -3862, -4038, -4212, -4382,
            -4552, -4718, -4880, -5040, -5198, -5352, -5502, -5648,
            -5792, -5934, -6070, -6204, -6332, -6458, -6580, -6698,
            -6812, -6922, -7026, -7128, -7226, -7318, -7406, -7490,
            -7568, -7644, -7714, -7778, -7840, -7896, -7946, -7994,
            -8034, -8072, -8104, -8130, -8152, -8170, -8182, -8190,
            -8192,
        };

        private static readonly Lazy<SmplMem> Instance = new Lazy<SmplMem>(BuildFromSeed);

        private readonly uint[] _bases;
        private readonly byte[][] _regions;

        private SmplMem(List<KeyValuePair<uint, byte[]>> regions, uint gcc, uint gnrg, uint gpitch, uint gclk)
        {
            _bases = new uint[regions.Count];
            _regions = new byte[regions.Count][];
            for (int i = 0; i < regions.Count; i++)
            {
                _bases[i] = regions[i].Key;
                _regions[i] = regions[i].Value;
            }

            GCC = gcc;
            GNrg = gnrg;
            GPitch = gpitch;
            GClk = gclk;
        }

        public uint GCC { get; }

        public uint GNrg { get; }

        public uint GPitch { get; }

        public uint GClk { get; }

        public int RegionCount => _regions.Length;

        /// <summary>The shared read-only Group-D window built once from the pitch seed.</summary>
        public static SmplMem Load()
        {
            return Instance.Value;
        }

        private static byte[] U16Bytes(uint[] v)
        {
            var b = new byte[v.Length * 2];
            for (int i = 0; i < v.Length; i++)
            {
                b[2 * i] = (byte)v[i];
                b[2 * i + 1] = (byte)(v[i] >> 8);
            }

            return b;
        }

        private static void Put32(List<byte> dst, uint x)
        {
            dst.Add((byte)x);
            dst.Add((byte)(x >> 8));
            dst.Add((byte)(x >> 16));
            dst.Add((byte)(x >> 24));
        }

        private static uint[] Flatten(uint[][] rows)
        {
            var o = new List<uint>();
            foreach (uint[] r in rows)
            {
                o.AddRange(r);
            }

            return o.ToArray();
        }

        private static SmplMem BuildFromSeed()
        {
            ContourWindowParts w = PitchSeed.BuildContourWindow();
            var regions = new List<KeyValuePair<uint, byte[]>>();

            var r0 = new List<byte>();
            foreach (int[][] rec in w.Records)
            {
                int[] blocks = rec[0];
                int[] seglens = rec[1];
                for (int i = 0; i < 8; i++)
                {
                    Put32(r0, (uint)(i < blocks.Length ? blocks[i] : 0));
                }

                for (int i = 0; i < 8; i++)
                {
                    Put32(r0, (uint)(i < seglens.Length ? seglens[i] : 0));
                }

                Put32(r0, (uint)blocks.Length);
            }

            Put32(r0, 187);
            foreach (uint h in new[] { MemNumContours, MemHdrContourMap, MemHdrLagCdf, MemHdrFracBase, MemHdrUnused[0], MemHdrUnused[1], MemHdrUnused[2], MemHdrDeltaCdf })
            {
                Put32(r0, h);
            }

            regions.Add(new KeyValuePair<uint, byte[]>(MemPcfg + 0x1d38, r0.ToArray()));
            regions.Add(new KeyValuePair<uint, byte[]>(MemHdrLagCdf, U16Bytes(w.LagCdf)));
            regions.Add(new KeyValuePair<uint, byte[]>(MemHdrFracBase, U16Bytes(Flatten(w.FracCmfs))));
            regions.Add(new KeyValuePair<uint, byte[]>(MemHdrDeltaCdf, U16Bytes(Flatten(w.DeltaCmfs))));
            regions.Add(new KeyValuePair<uint, byte[]>(MemHdrContourMap, (byte[])w.ContourMap.Clone()));

            var bounds = new List<byte>(w.FirstblockRange.Length * 2 + 2);
            foreach (int[] p in w.FirstblockRange)
            {
                bounds.Add((byte)p[0]);
                bounds.Add((byte)p[1]);
            }

            bounds.Add(0);
            bounds.Add(0);
            regions.Add(new KeyValuePair<uint, byte[]>(MemDeltaBounds, bounds.ToArray()));

            return new SmplMem(regions, 0, 0, MemGPitch, MemGClk);
        }

        private bool RegionFor(uint addr, int n, out byte[] data, out int off)
        {
            for (int i = 0; i < _regions.Length; i++)
            {
                if (addr >= _bases[i] && (long)(addr - _bases[i]) + n <= _regions[i].Length)
                {
                    data = _regions[i];
                    off = (int)(addr - _bases[i]);
                    return true;
                }
            }

            data = null;
            off = 0;
            return false;
        }

        public byte U8(uint addr)
        {
            byte[] d;
            int o;
            return RegionFor(addr, 1, out d, out o) ? d[o] : (byte)0;
        }

        public ushort U16(uint addr)
        {
            byte[] d;
            int o;
            return RegionFor(addr, 2, out d, out o) ? (ushort)(d[o] | d[o + 1] << 8) : (ushort)0;
        }

        public short I16(uint addr)
        {
            return unchecked((short)U16(addr));
        }

        public uint U32(uint addr)
        {
            byte[] d;
            int o;
            if (RegionFor(addr, 4, out d, out o))
            {
                return (uint)(d[o] | d[o + 1] << 8 | d[o + 2] << 16 | d[o + 3] << 24);
            }

            return 0;
        }

        public int I32(uint addr)
        {
            return unchecked((int)U32(addr));
        }

        /// <summary>The n-entry cumulative uint16 CDF at addr; entries outside the window read as 0.</summary>
        public ushort[] CdfAt(uint addr, int n)
        {
            var o = new ushort[n];
            for (int i = 0; i < n; i++)
            {
                o[i] = U16(unchecked(addr + (uint)i * 2));
            }

            return o;
        }
    }
}
