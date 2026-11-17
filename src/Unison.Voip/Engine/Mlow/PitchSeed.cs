// =============================================================================
// PitchSeed
//
// Build-from-seed for the MLow pitch runtime tables: the range-coded blocksegs
// bitstream, the index maps and integer DCMF->CDF expansion of pitch_seed.bin.
// Also yields the Group-D contour window parts that SmplMem lays out.
// Ports: meowcaller mlow/pitch_seed.go (MIT)
// =============================================================================
using System.Collections.Generic;

namespace Unison.Voip.Engine.Mlow
{
    internal sealed class PitchSeedData
    {
        public byte[] BlocksegsBitstream;
        public byte[] Blocksegs2Idx;
        public byte[] BlocksegsIx;
        public byte[] FirstblockRange;
        public byte[] BlocksegIdxDcmf;
        public byte[] DeltaLagDcmfs;
        public byte[] BlockTransitionDcmf;
    }

    internal sealed class ContourWindowParts
    {
        public int[][][] Records;
        public byte[] ContourMap;
        public int[][] FirstblockRange;
        public uint[] LagCdf;
        public uint[][] FracCmfs;
        public uint[][] DeltaCmfs;
    }

    internal static class PitchSeed
    {
        public const int NumBlocksegs = 217;
        public const int NumBlocktracks = 187;

        public static PitchSeedData Load()
        {
            Dictionary<int, byte[]> f = SeedProto.ParseProtoBytes(SeedProto.LoadInflated("pitch_seed.bin"));
            return new PitchSeedData
            {
                BlocksegsBitstream = SeedProto.BytesOf(f, 1),
                Blocksegs2Idx = SeedProto.BytesOf(f, 2),
                BlocksegsIx = SeedProto.BytesOf(f, 3),
                FirstblockRange = SeedProto.BytesOf(f, 4),
                BlocksegIdxDcmf = SeedProto.BytesOf(f, 5),
                DeltaLagDcmfs = SeedProto.BytesOf(f, 6),
                BlockTransitionDcmf = SeedProto.BytesOf(f, 7),
            };
        }

        /// <summary>Uniform symbol in [0, n).</summary>
        public static uint EcDecodeUniform(RangeDecoder dec, uint n)
        {
            uint v = dec.Decode(n);
            dec.Update(v, v + 1, n);
            return v;
        }

        /// <summary>len = uniform(6)+1, then len pairs of (uniform(9), uniform(4)+1).</summary>
        public static PitchBlockSeg DecodeBlockseg(RangeDecoder dec)
        {
            int length = (int)(EcDecodeUniform(dec, 6) + 1);
            var blocks = new int[length];
            var seglens = new int[length];
            for (int j = 0; j < length; j++)
            {
                blocks[j] = (int)EcDecodeUniform(dec, 9);
                seglens[j] = (int)(EcDecodeUniform(dec, 4) + 1);
            }

            return new PitchBlockSeg { Nblocks = length, Blocks = blocks, Seglens = seglens };
        }

        public static PitchBlockTrack[] GenBlocktracks(PitchBlockSeg[] blocksegs, int[][] blocksegsIx)
        {
            var o = new PitchBlockTrack[NumBlocktracks];
            for (int trackIdx = 0; trackIdx < NumBlocktracks; trackIdx++)
            {
                PitchBlockSeg seg = blocksegs[blocksegsIx[trackIdx][0]];
                var track = new int[SmplPitch.NumSubframes];
                int segIdx = 0;
                float meanblock = 0, trackdeltas = 0;
                for (int b = 0; b < seg.Nblocks; b++)
                {
                    for (int k = 0; k < seg.Seglens[b]; k++)
                    {
                        track[segIdx] = seg.Blocks[b];
                        segIdx++;
                    }

                    meanblock += (float)(seg.Blocks[b] * seg.Seglens[b]);
                    if (b != 0)
                    {
                        int d = seg.Blocks[b - 1] - seg.Blocks[b];
                        if (d < 0)
                        {
                            d = -d;
                        }

                        trackdeltas += (float)d;
                    }
                }

                meanblock /= (float)SmplPitch.NumSubframes;
                o[trackIdx] = new PitchBlockTrack { Track = track, Meanblock = meanblock, Trackdeltas = trackdeltas };
            }

            return o;
        }

        /// <summary>Integer DCMF expansion to a cumulative CDF of length len+1.</summary>
        public static uint[] PitchDcmfToCmf(byte[] dcmf, int offset, int n)
        {
            var cmf = new uint[n + 1];
            long sum = 0;
            for (int i = 0; i < n; i++)
            {
                int tmp = dcmf[offset + i] + 1;
                tmp *= tmp;
                if (tmp > 65535)
                {
                    tmp = 65535;
                }

                cmf[i + 1] = (uint)tmp;
                sum += tmp;
            }

            cmf[0] = 0;
            for (int i = 1; i <= n; i++)
            {
                long prev = cmf[i - 1];
                long add = cmf[i] * (32767L - n) / sum + 1;
                cmf[i] = unchecked((uint)(prev + add));
            }

            return cmf;
        }

        public static int[][] ChunkPairs(byte[] b)
        {
            var o = new List<int[]>(b.Length / 2);
            for (int i = 0; i + 1 < b.Length; i += 2)
            {
                o.Add(new int[] { b[i], b[i + 1] });
            }

            return o.ToArray();
        }

        private static uint[][] DcmfRows(byte[] b, int step)
        {
            var o = new List<uint[]>();
            for (int i = 0; i + step <= b.Length; i += step)
            {
                o.Add(PitchDcmfToCmf(b, i, step));
            }

            return o.ToArray();
        }

        public static ContourWindowParts BuildContourWindow()
        {
            PitchSeedData s = Load();
            var dec = new RangeDecoder(s.BlocksegsBitstream);
            var records = new int[NumBlocksegs][][];
            for (int i = 0; i < NumBlocksegs; i++)
            {
                PitchBlockSeg bs = DecodeBlockseg(dec);
                records[i] = new[] { bs.Blocks, bs.Seglens };
            }

            return new ContourWindowParts
            {
                Records = records,
                ContourMap = s.Blocksegs2Idx,
                FirstblockRange = ChunkPairs(s.FirstblockRange),
                LagCdf = PitchDcmfToCmf(s.BlocksegIdxDcmf, 0, s.BlocksegIdxDcmf.Length),
                FracCmfs = DcmfRows(s.DeltaLagDcmfs, 319),
                DeltaCmfs = DcmfRows(s.BlockTransitionDcmf, SmplPitch.PitchNumBlocks),
            };
        }

        public static PitchTables BuildPitchTablesFromSeed()
        {
            PitchSeedData s = Load();
            var dec = new RangeDecoder(s.BlocksegsBitstream);
            var blocksegs = new PitchBlockSeg[NumBlocksegs];
            for (int i = 0; i < NumBlocksegs; i++)
            {
                blocksegs[i] = DecodeBlockseg(dec);
            }

            int[][] blocksegsIx = ChunkPairs(s.BlocksegsIx);
            int[][] firstblockRange = ChunkPairs(s.FirstblockRange);
            PitchBlockTrack[] blocktracks = GenBlocktracks(blocksegs, blocksegsIx);
            var blocksegs2Idx = new int[s.Blocksegs2Idx.Length];
            for (int i = 0; i < blocksegs2Idx.Length; i++)
            {
                blocksegs2Idx[i] = s.Blocksegs2Idx[i];
            }

            return new PitchTables
            {
                Blocksegs = blocksegs,
                Blocktracks = blocktracks,
                Blocksegs2Idx = blocksegs2Idx,
                BlocksegIdxCmf = PitchDcmfToCmf(s.BlocksegIdxDcmf, 0, s.BlocksegIdxDcmf.Length),
                DeltaLagCmfs = DcmfRows(s.DeltaLagDcmfs, 319),
                BlocksegsIx = blocksegsIx,
                FirstblockRange = firstblockRange,
                BlockTransitionCmf = DcmfRows(s.BlockTransitionDcmf, SmplPitch.PitchNumBlocks),
            };
        }
    }
}
