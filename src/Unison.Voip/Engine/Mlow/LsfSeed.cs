// =============================================================================
// LsfSeed
//
// Build-from-seed for the MLow LSF runtime tables (SmplSynthTables, SmplTables,
// LsfCb) from lsf_seed.bin. The float op order is load-bearing (matmul
// accumulation, sqrt-then-reciprocal, integer truncation in the DCMF expansion,
// scalar unpack8) so the rebuilt tables are bit-faithful.
// Ports: meowcaller mlow/lsf_seed.go (MIT)
// =============================================================================
using System;
using System.Collections.Generic;

namespace Unison.Voip.Engine.Mlow
{
    /// <summary>The three LSF runtime structs rebuilt from one seed.</summary>
    internal sealed class LsfBuilt
    {
        public SmplSynthTables Synth;
        public SmplTables Tables;
        public LsfCb Cb;
    }

    internal static class LsfSeed
    {
        private const int Order = MlowMath.SmplLpcOrder;
        private const int Centroids = LsfQuant.LsfCbCentroids;
        private const int CinvLen = Order * (Order + 1) / 2;
        private const int St2Len = 9593;

        private static readonly float[] CbMin = { -0.5873778f, -0.24721986f };
        private static readonly float[] CbScale = { 1.3145164e-5f, 7.226229e-6f };
        private static readonly float[] CinvMin = { -3.5960955e-5f, -2.778548e-5f };
        private static readonly float[] CinvScale = { 1.8589316e-9f, 1.2180106e-9f };
        private static readonly float[] RotMin = { -0.9124832f, -0.8455929f };
        private static readonly float[] RotScale = { 0.006554049f, 0.0069253775f };
        private static readonly float[] RotCondMin = { -0.67291605f, -0.8248211f };
        private static readonly float[] RotCondScale = { 0.0052386564f, 0.0064186584f };

        private const float St2QlvlsMin = -0.45f;
        private const float St2QlvlsScale = 0.0034478905f;

        private static readonly Lazy<LsfBuilt> Built = new Lazy<LsfBuilt>(() => Build(LoadSeed()));

        /// <summary>The packed ROM reshaped into the nested arrays the expansion indexes; outer index [voiced].</summary>
        private sealed class Seed
        {
            public ushort[,,] Cb16 = new ushort[2, Centroids, Order];
            public ushort[,] Cinv16 = new ushort[2, CinvLen];
            public byte[,,,] Rot8 = new byte[2, Centroids, Order, Order];
            public byte[,,,] RotCond8 = new byte[2, 2, Order, Order];
            public float[][] Mean = { new float[Order], new float[Order] };
            public ushort[][] Cmf = { new ushort[17], new ushort[17] };
            public ushort[][] CmfCond = { new ushort[18], new ushort[18] };
            public float[][] MinDist = { new float[17], new float[17] };
            public float[] RegCond = new float[2];
            public sbyte[,,,] MinQi = new sbyte[2, 2, 17, Order];
            public sbyte[,,,] MaxQi = new sbyte[2, 2, 17, Order];
            public float[,] Qstep = new float[2, 2];
            public byte[] St2Qlvls8;
            public byte[] St2Dcmfs;
            public ushort[,] LsfSel = new ushort[3, 3];
            public ushort[] LsfExtra = new ushort[3];
        }

        public static LsfBuilt LoadLsfBuilt()
        {
            return Built.Value;
        }

        private static Seed LoadSeed()
        {
            Dictionary<int, ProtoField> f = SeedProto.ParseProto(SeedProto.LoadInflated("lsf_seed.bin"));
            var s = new Seed();

            byte[] rot = SeedProto.BytesOf(f, 1);
            int p = 0;
            for (int v = 0; v < 2; v++)
            {
                for (int c = 0; c < Centroids; c++)
                {
                    for (int i = 0; i < Order; i++)
                    {
                        for (int j = 0; j < Order; j++)
                        {
                            s.Rot8[v, c, i, j] = rot[p++];
                        }
                    }
                }
            }

            byte[] rotCond = SeedProto.BytesOf(f, 2);
            p = 0;
            for (int v = 0; v < 2; v++)
            {
                for (int lr = 0; lr < 2; lr++)
                {
                    for (int i = 0; i < Order; i++)
                    {
                        for (int j = 0; j < Order; j++)
                        {
                            s.RotCond8[v, lr, i, j] = rotCond[p++];
                        }
                    }
                }
            }

            s.St2Qlvls8 = SeedProto.BytesOf(f, 3);
            s.St2Dcmfs = SeedProto.BytesOf(f, 4);

            byte[][] qiSrc = { SeedProto.BytesOf(f, 5), SeedProto.BytesOf(f, 6) };
            for (int idx = 0; idx < 2; idx++)
            {
                p = 0;
                for (int v = 0; v < 2; v++)
                {
                    for (int lr = 0; lr < 2; lr++)
                    {
                        for (int c = 0; c < 17; c++)
                        {
                            for (int i = 0; i < Order; i++)
                            {
                                sbyte q = unchecked((sbyte)qiSrc[idx][p++]);
                                if (idx == 0)
                                {
                                    s.MinQi[v, lr, c, i] = q;
                                }
                                else
                                {
                                    s.MaxQi[v, lr, c, i] = q;
                                }
                            }
                        }
                    }
                }
            }

            uint[] cb = SeedProto.DecodeVarintsU32(SeedProto.BytesOf(f, 7));
            p = 0;
            for (int v = 0; v < 2; v++)
            {
                for (int c = 0; c < Centroids; c++)
                {
                    for (int i = 0; i < Order; i++)
                    {
                        s.Cb16[v, c, i] = unchecked((ushort)cb[p++]);
                    }
                }
            }

            uint[] cinv = SeedProto.DecodeVarintsU32(SeedProto.BytesOf(f, 8));
            p = 0;
            for (int v = 0; v < 2; v++)
            {
                for (int i = 0; i < CinvLen; i++)
                {
                    s.Cinv16[v, i] = unchecked((ushort)cinv[p++]);
                }
            }

            uint[] cmf = SeedProto.DecodeVarintsU32(SeedProto.BytesOf(f, 9));
            p = 0;
            for (int v = 0; v < 2; v++)
            {
                for (int i = 0; i < 17; i++)
                {
                    s.Cmf[v][i] = unchecked((ushort)cmf[p++]);
                }
            }

            uint[] cmfc = SeedProto.DecodeVarintsU32(SeedProto.BytesOf(f, 10));
            p = 0;
            for (int v = 0; v < 2; v++)
            {
                for (int i = 0; i < 18; i++)
                {
                    s.CmfCond[v][i] = unchecked((ushort)cmfc[p++]);
                }
            }

            uint[] sel = SeedProto.DecodeVarintsU32(SeedProto.BytesOf(f, 11));
            p = 0;
            for (int a = 0; a < 3; a++)
            {
                for (int b = 0; b < 3; b++)
                {
                    s.LsfSel[a, b] = unchecked((ushort)sel[p++]);
                }
            }

            uint[] ex = SeedProto.DecodeVarintsU32(SeedProto.BytesOf(f, 12));
            for (int i = 0; i < 3; i++)
            {
                s.LsfExtra[i] = unchecked((ushort)ex[i]);
            }

            float[] mean = SeedProto.DecodeFloats(SeedProto.BytesOf(f, 13));
            p = 0;
            for (int v = 0; v < 2; v++)
            {
                for (int i = 0; i < Order; i++)
                {
                    s.Mean[v][i] = mean[p++];
                }
            }

            float[] md = SeedProto.DecodeFloats(SeedProto.BytesOf(f, 14));
            p = 0;
            for (int v = 0; v < 2; v++)
            {
                for (int i = 0; i < 17; i++)
                {
                    s.MinDist[v][i] = md[p++];
                }
            }

            float[] rc = SeedProto.DecodeFloats(SeedProto.BytesOf(f, 15));
            s.RegCond[0] = rc[0];
            s.RegCond[1] = rc[1];

            float[] qs = SeedProto.DecodeFloats(SeedProto.BytesOf(f, 16));
            p = 0;
            for (int v = 0; v < 2; v++)
            {
                for (int i = 0; i < 2; i++)
                {
                    s.Qstep[v, i] = qs[p++];
                }
            }

            return s;
        }

        // ---- float expansion primitives (op order is load-bearing) ----

        /// <summary>y[i] = sum_j C[j][i] * x[j], accumulator seeded at j=0; products rounded before the add.</summary>
        private static float[] MatMultTransp16(float[][] c, float[] x)
        {
            var y = new float[Order];
            float x0 = x[0];
            for (int i = 0; i < Order; i++)
            {
                y[i] = c[0][i] * x0;
            }

            for (int j = 1; j < Order; j++)
            {
                float xj = x[j];
                for (int i = 0; i < Order; i++)
                {
                    float prod = c[j][i] * xj;
                    y[i] += prod;
                }
            }

            return y;
        }

        /// <summary>Laroia inverse-gap LSF weights, gap floored at 1e-3.</summary>
        private static float[] SeedLaroia(float[] lsf)
        {
            const float minDist = 1e-3f;
            var inv = new float[Order + 1];
            inv[0] = 1.0f / MlowMath.MaxF32(lsf[0], minDist);
            for (int i = 1; i < Order; i++)
            {
                inv[i] = 1.0f / MlowMath.MaxF32(lsf[i] - lsf[i - 1], minDist);
            }

            inv[Order] = 1.0f / MlowMath.MaxF32(MlowMath.SmplPi - lsf[Order - 1], minDist);
            var w = new float[Order];
            for (int i = 0; i < Order; i++)
            {
                w[i] = inv[i] + inv[i + 1];
            }

            return w;
        }

        /// <summary>lsfw = sqrt(laroia(lsf)); we[i][j] = rot[i][j]/lsfw[j], wie[j][i] = rot[i][j]*lsfw[j].</summary>
        private static void RotApplyWght(float[][] rot, float[] lsf, out float[][] we, out float[][] wie)
        {
            float[] lsfw = SeedLaroia(lsf);
            for (int i = 0; i < Order; i++)
            {
                lsfw[i] = MlowMath.SqrtF32(lsfw[i]);
            }

            var lsfwInv = new float[Order];
            for (int i = 0; i < Order; i++)
            {
                lsfwInv[i] = 1.0f / lsfw[i];
            }

            we = NewMat16();
            wie = NewMat16();
            for (int i = 0; i < Order; i++)
            {
                for (int j = 0; j < Order; j++)
                {
                    we[i][j] = rot[i][j] * lsfwInv[j];
                    wie[j][i] = rot[i][j] * lsfw[j];
                }
            }
        }

        /// <summary>bits[i] = -log2f((cmf[i+1]-cmf[i]) / cmf[len-1]).</summary>
        private static float[] CmfToBits(ushort[] cmf)
        {
            int n = cmf.Length;
            float den = cmf[n - 1];
            var bits = new float[n - 1];
            for (int i = 0; i < n - 1; i++)
            {
                float num = (float)(cmf[i + 1] - cmf[i]);
                bits[i] = -MlowMath.Log2F32(num / den);
            }

            return bits;
        }

        /// <summary>Integer delta-CMF expansion to a cumulative u16 CDF of length len+1.</summary>
        private static ushort[] DcmfToCmf(byte[] dcmf, int offset, int n)
        {
            var cmf = new ushort[n + 1];
            long sum = 0;
            for (int i = 0; i < n; i++)
            {
                int tmp = dcmf[offset + i] + 1;
                tmp *= tmp;
                if (tmp > 65535)
                {
                    tmp = 65535;
                }

                cmf[i + 1] = (ushort)tmp;
                sum += tmp;
            }

            cmf[0] = 0;
            for (int i = 1; i < n + 1; i++)
            {
                long prev = cmf[i - 1];
                long add = cmf[i] * (long)(32767 - n) / sum + 1;
                cmf[i] = unchecked((ushort)(prev + add));
            }

            return cmf;
        }

        /// <summary>out[i][j] = min + (packed[i][j]*scale), product rounded first.</summary>
        private static float[][] Unpack8(byte[,,,] packed, int a, int b, float scale, float min)
        {
            float[][] o = NewMat16();
            for (int i = 0; i < Order; i++)
            {
                for (int j = 0; j < Order; j++)
                {
                    float prod = (float)packed[a, b, i, j] * scale;
                    o[i][j] = min + prod;
                }
            }

            return o;
        }

        private static float[][] NewMat16()
        {
            var m = new float[Order][];
            for (int i = 0; i < Order; i++)
            {
                m[i] = new float[Order];
            }

            return m;
        }

        private static float[][] CloneMat(float[][] m)
        {
            var o = new float[m.Length][];
            for (int i = 0; i < m.Length; i++)
            {
                o[i] = (float[])m[i].Clone();
            }

            return o;
        }

        private static LsfBuilt Build(Seed s)
        {
            var st1 = new St1Tables[2];
            var synthCentroids = new float[2][][];
            var synthMatrices = new float[2][][][];
            var synthGrid16Matrices = new float[2][][];

            for (int voiced = 0; voiced < 2; voiced++)
            {
                float[][] cInv = NewMat16();
                int p = 0;
                for (int i = 0; i < Order; i++)
                {
                    for (int j = 0; j <= i; j++)
                    {
                        float prod = CinvScale[voiced] * (float)s.Cinv16[voiced, p];
                        float v = CinvMin[voiced] + prod;
                        cInv[i][j] = v;
                        cInv[j][i] = v;
                        p++;
                    }
                }

                var cbhalf = new float[Centroids][];
                var cbCinv = new float[Centroids][];
                var we = new float[Centroids][][];
                var wie = new float[Centroids][][];
                for (int c = 0; c < Centroids; c++)
                {
                    var lsfCb = new float[Order];
                    cbhalf[c] = new float[Order];
                    for (int i = 0; i < Order; i++)
                    {
                        float prod = (float)s.Cb16[voiced, c, i] * CbScale[voiced];
                        lsfCb[i] = CbMin[voiced] + prod + s.Mean[voiced][i];
                        cbhalf[c][i] = lsfCb[i] * 0.5f;
                    }

                    cbCinv[c] = MatMultTransp16(cInv, lsfCb);
                    float[][] rot = Unpack8(s.Rot8, voiced, c, RotScale[voiced], RotMin[voiced]);
                    RotApplyWght(rot, lsfCb, out we[c], out wie[c]);
                }

                var rotcond = new float[2][][];
                for (int lr = 0; lr < 2; lr++)
                {
                    rotcond[lr] = Unpack8(s.RotCond8, voiced, lr, RotCondScale[voiced], RotCondMin[voiced]);
                }

                st1[voiced] = new St1Tables
                {
                    Cbhalf = cbhalf,
                    CInv = cInv,
                    BitsCond = CmfToBits(s.CmfCond[voiced]),
                    Rotcond = new[] { CloneMat(rotcond[0]), CloneMat(rotcond[1]) },
                    CbCinv = cbCinv,
                    We = we,
                    Bits = CmfToBits(s.Cmf[voiced]),
                    Wie = wie,
                };

                var sc = new float[Centroids][];
                var sm = new float[Centroids][][];
                for (int g = 0; g < Centroids; g++)
                {
                    sc[g] = (float[])cbhalf[g].Clone();
                    sm[g] = CloneMat(we[g]);
                }

                synthCentroids[voiced] = sc;
                synthMatrices[voiced] = sm;

                var g16 = new float[2][];
                for (int lr = 0; lr < 2; lr++)
                {
                    var flat = new float[Order * Order];
                    for (int i = 0; i < Order; i++)
                    {
                        Array.Copy(rotcond[lr][i], 0, flat, i * Order, Order);
                    }

                    g16[lr] = flat;
                }

                synthGrid16Matrices[voiced] = g16;
            }

            var qlvlsFlat = new float[St2Len];
            var numqlvlsFlat = new int[2, 2, 17, Order];
            var qoffFlat = new int[2, 2, 17, Order];
            var cmfSlices = new ushort[2, 2, 17, Order][];
            var numbitsSlices = new float[2, 2, 17, Order][];

            int qPtr = 0, q8Ptr = 0, dcmfPtr = 0;
            for (int voiced = 0; voiced < 2; voiced++)
            {
                for (int lr = 0; lr < 2; lr++)
                {
                    for (int c = 0; c < Centroids + 1; c++)
                    {
                        float qstep = s.Qstep[voiced, lr];
                        if (c == Centroids)
                        {
                            qstep *= LsfQuant.LsfQstepCondMult;
                        }

                        for (int i = 0; i < Order; i++)
                        {
                            int minQi = s.MinQi[voiced, lr, c, i];
                            int maxQi = s.MaxQi[voiced, lr, c, i];
                            int numQlvls = maxQi - minQi + 1;
                            numqlvlsFlat[voiced, lr, c, i] = numQlvls;
                            qoffFlat[voiced, lr, c, i] = qPtr;
                            for (int lvl = 0; lvl < numQlvls; lvl++)
                            {
                                float q8 = s.St2Qlvls8[q8Ptr];
                                float prod = St2QlvlsScale * q8;
                                qlvlsFlat[qPtr] = (St2QlvlsMin + prod + (float)lvl + (float)minQi) * qstep;
                                qPtr++;
                                q8Ptr++;
                            }

                            ushort[] cmf = DcmfToCmf(s.St2Dcmfs, dcmfPtr, numQlvls);
                            dcmfPtr += numQlvls;
                            cmfSlices[voiced, lr, c, i] = cmf;
                            numbitsSlices[voiced, lr, c, i] = CmfToBits(cmf);
                        }
                    }
                }
            }

            if (qPtr != St2Len || q8Ptr != St2Len || dcmfPtr != St2Len)
            {
                throw new InvalidOperationException("mlow: lsf seed stage-2 pointer miscount (corrupt seed)");
            }

            var st2 = new St2Tables[2][][];
            var valtables = new float[2][][][][];
            var lsfStage2 = new ushort[2][][][][];
            for (int voiced = 0; voiced < 2; voiced++)
            {
                st2[voiced] = new St2Tables[2][];
                valtables[voiced] = new float[2][][][];
                lsfStage2[voiced] = new ushort[2][][][];
                for (int lr = 0; lr < 2; lr++)
                {
                    st2[voiced][lr] = new St2Tables[Centroids + 1];
                    valtables[voiced][lr] = new float[Centroids + 1][][];
                    lsfStage2[voiced][lr] = new ushort[Centroids + 1][][];
                    for (int c = 0; c < Centroids + 1; c++)
                    {
                        var nq = new int[Order];
                        var qlvls = new float[Order][];
                        var vt = new float[Order][];
                        var nb = new float[Order][];
                        var cmfRows = new ushort[Order][];
                        for (int i = 0; i < Order; i++)
                        {
                            int n = numqlvlsFlat[voiced, lr, c, i];
                            int off = qoffFlat[voiced, lr, c, i];
                            nq[i] = n;
                            qlvls[i] = new float[n];
                            Array.Copy(qlvlsFlat, off, qlvls[i], 0, n);
                            vt[i] = new float[n];
                            Array.Copy(qlvlsFlat, off, vt[i], 0, n);
                            nb[i] = numbitsSlices[voiced, lr, c, i];
                            cmfRows[i] = cmfSlices[voiced, lr, c, i];
                        }

                        st2[voiced][lr][c] = new St2Tables { NumQlvls = nq, Qlvls = qlvls, NumBits = nb };
                        valtables[voiced][lr][c] = vt;
                        lsfStage2[voiced][lr][c] = cmfRows;
                    }
                }
            }

            var cbOut = new LsfCb
            {
                St1 = st1,
                St2 = st2,
                MinQi = CloneQi(s.MinQi),
                MaxQi = CloneQi(s.MaxQi),
                Qstep = new[] { new[] { s.Qstep[0, 0], s.Qstep[0, 1] }, new[] { s.Qstep[1, 0], s.Qstep[1, 1] } },
                MeanV = (float[])s.Mean[1].Clone(),
                MeanUV = (float[])s.Mean[0].Clone(),
                RegCond = new[] { s.RegCond[0], s.RegCond[1] },
                MinDistV = (float[])s.MinDist[1].Clone(),
                MinDistUV = (float[])s.MinDist[0].Clone(),
            };

            var tables = new SmplTables
            {
                LsfSel = new[]
                {
                    new[] { s.LsfSel[0, 0], s.LsfSel[0, 1], s.LsfSel[0, 2] },
                    new[] { s.LsfSel[1, 0], s.LsfSel[1, 1], s.LsfSel[1, 2] },
                    new[] { s.LsfSel[2, 0], s.LsfSel[2, 1], s.LsfSel[2, 2] },
                },
                LsfGrid = new LsfGrid
                {
                    Match1 = (ushort[])s.CmfCond[1].Clone(),
                    Match1Alt = (ushort[])s.CmfCond[0].Clone(),
                    Match0 = (ushort[])s.Cmf[0].Clone(),
                    Match0Alt = (ushort[])s.Cmf[1].Clone(),
                },
                LsfStage2 = lsfStage2,
                LsfExtra = new[] { s.LsfExtra[0], s.LsfExtra[1], s.LsfExtra[2] },
            };

            var synth = new SmplSynthTables
            {
                Valtables = valtables,
                Centroids = synthCentroids,
                Matrices = synthMatrices,
                MinSpacing = new[] { (float[])s.MinDist[1].Clone(), (float[])s.MinDist[0].Clone() },
                Grid16W = new[] { (float[])s.Mean[1].Clone(), (float[])s.Mean[0].Clone() },
                Grid16Alpha = new[] { s.RegCond[0], s.RegCond[1] },
                Grid16Matrices = synthGrid16Matrices,
            };

            return new LsfBuilt { Synth = synth, Tables = tables, Cb = cbOut };
        }

        private static int[][][][] CloneQi(sbyte[,,,] qi)
        {
            var o = new int[2][][][];
            for (int v = 0; v < 2; v++)
            {
                o[v] = new int[2][][];
                for (int lr = 0; lr < 2; lr++)
                {
                    o[v][lr] = new int[17][];
                    for (int c = 0; c < 17; c++)
                    {
                        var row = new int[Order];
                        for (int i = 0; i < Order; i++)
                        {
                            row[i] = qi[v, lr, c, i];
                        }

                        o[v][lr][c] = row;
                    }
                }
            }

            return o;
        }
    }
}
