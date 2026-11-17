// =============================================================================
// SmplLsf
//
// Per-internal-frame LSF index decode (Meta-specific, not stock SILK CB1): a
// 2-way stage-1 selector, a stage-1 grid index, 16 stage-2 residuals and the
// 3-symbol extra CDF, plus the cross-frame predictor state.
// Ports: meowcaller mlow/lsf.go (MIT)
// =============================================================================
namespace Unison.Voip.Engine.Mlow
{
    /// <summary>The four stage-1 grid CDFs, selected by (match, stage1 != 0).</summary>
    internal sealed class LsfGrid
    {
        public ushort[] Match1;
        public ushort[] Match1Alt;
        public ushort[] Match0;
        public ushort[] Match0Alt;
    }

    /// <summary>Runtime LSF CDF table set; LsfStage2 is [stage1][config][grid][coeff] -&gt; CDF.</summary>
    internal sealed class SmplTables
    {
        public ushort[][] LsfSel;
        public LsfGrid LsfGrid;
        public ushort[][][][][] LsfStage2;
        public ushort[] LsfExtra;
    }

    /// <summary>
    /// Cross-internal-frame LSF/pitch predictor state. PrevLagSamples, PrevLagblk and
    /// PrevLagidx are encoder-only and unused by the decoder.
    /// </summary>
    internal sealed class SmplLsfState
    {
        public int PrevStage1;
        public bool PrevMatch;
        public bool HavePrev;
        public int PrevGainIdx;
        public int PrevFiltIdx;
        public int PrevLag;
        public int PrevFracLag;
        public float PrevLagSamples;
        public int PrevLagblk;
        public int PrevLagidx;
    }

    /// <summary>Decoded LSF index set; StageNraw[k] is len(cdf)-2 for coefficient k.</summary>
    internal sealed class SmplLsfIndices
    {
        public int Stage1;
        public int Grid;
        public readonly int[] Stage2 = new int[16];
        public readonly int[] StageNraw = new int[16];
        public int Extra;
    }

    internal static class SmplLsf
    {
        public static SmplTables LoadSmplTables()
        {
            return LsfSeed.LoadLsfBuilt().Tables;
        }

        /// <summary>
        /// Advances the LSF predictor mirror for an internal frame with the given stage-1:
        /// a no-match (intf 0 or a changed stage1) resets the pitch/LTP predictor to -1.
        /// </summary>
        public static void SmplAdvanceLsfState(SmplLsfState st, int intf, int stage1)
        {
            bool m = intf != 0 && stage1 == st.PrevStage1;
            if (!m)
            {
                st.PrevGainIdx = -1;
                st.PrevFiltIdx = -1;
                st.PrevLag = -1;
                st.PrevFracLag = -1;
                st.PrevLagblk = -1;
                st.PrevLagidx = -1;
            }

            st.PrevStage1 = stage1;
            st.PrevMatch = m;
            st.HavePrev = true;
        }

        /// <summary>
        /// Decodes the LSF block of one internal frame. config is the smpl config (0/1);
        /// intf is the internal-frame index (0,1,2). Mutates st with the no-match reset.
        /// </summary>
        public static SmplLsfIndices DecodeSmplLsf(RangeDecoder dec, SmplTables t, SmplLsfState st, int config, int intf)
        {
            var idx = new SmplLsfIndices();

            int sel = 0;
            if (intf != 0)
            {
                sel = st.PrevStage1 != 0 ? 2 : 1;
            }

            int stage1 = dec.DecodeCdf(t.LsfSel[sel]);
            idx.Stage1 = stage1;

            bool m = intf != 0 && stage1 == st.PrevStage1;
            if (!m)
            {
                st.PrevGainIdx = -1;
                st.PrevFiltIdx = -1;
                st.PrevLag = -1;
                st.PrevFracLag = -1;
            }

            st.PrevStage1 = stage1;

            ushort[] gridCdf;
            if (m && stage1 != 0)
            {
                gridCdf = t.LsfGrid.Match1;
            }
            else if (m)
            {
                gridCdf = t.LsfGrid.Match1Alt;
            }
            else if (stage1 != 0)
            {
                gridCdf = t.LsfGrid.Match0Alt;
            }
            else
            {
                gridCdf = t.LsfGrid.Match0;
            }

            int grid = dec.DecodeCdf(gridCdf);
            idx.Grid = grid;
            st.PrevMatch = m;
            st.HavePrev = true;

            ushort[][] st2 = t.LsfStage2[stage1][config][grid];
            for (int k = 0; k < 16; k++)
            {
                ushort[] c = st2[k];
                idx.Stage2[k] = dec.DecodeCdf(c);
                idx.StageNraw[k] = c.Length - 2;
            }

            idx.Extra = dec.DecodeCdf(t.LsfExtra);
            return idx;
        }
    }
}
