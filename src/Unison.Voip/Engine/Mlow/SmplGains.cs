// =============================================================================
// SmplGains
//
// Per-subframe gains + energy-residual decode for unvoiced internal frames
// (LSF stage-1 selector 0), mutually exclusive with the pitch block.
// Ports: meowcaller mlow/gains.go (MIT)
// =============================================================================
namespace Unison.Voip.Engine.Mlow
{
    /// <summary>Decoded per-subframe gains, energy-residual symbols and the raw gain symbols.</summary>
    internal sealed class SmplGainResult
    {
        public readonly int[] GainQ = new int[4];
        public readonly int[] NrgRes = new int[4];
        public int GainMain;
        public int GainDelta;
    }

    internal static class SmplGains
    {
        /// <summary>
        /// Decodes the gains + nrgres reads; subfrCounts are the per-subframe pulse counts.
        /// mem is kept for call-site stability (tables come from CcTables).
        /// </summary>
        public static SmplGainResult DecodeSmplGains(RangeDecoder dec, SmplMem mem, int p3, int[] subfrCounts)
        {
            var res = new SmplGainResult();
            CcTables cc = CcTables.Load();

            int gainMain = dec.DecodeCdf(cc.NrgresGain(p3));
            int gainDelta = dec.DecodeCdf(cc.NrgresShape(p3));
            res.GainMain = gainMain;
            res.GainDelta = gainDelta;
            int cfgSel = p3 == 2 ? 1 : 2;

            int off6 = p3 * gainDelta;
            int base7 = unchecked(gainMain * cc.NrgStep(cfgSel) - 0x154000);
            int take = p3 > 4 ? 4 : p3;
            for (int sf = 0; sf < take; sf++)
            {
                int cbv = cc.GainRecon(p3 == 4, sf + off6);
                res.GainQ[sf] = unchecked(base7 + (cbv << 4));
            }

            for (int sf = 0; sf < take; sf++)
            {
                int cnt = subfrCounts[sf];
                if (cnt <= 0)
                {
                    continue;
                }

                int bucket = cnt >= 30 ? 3 : (cnt & 0xffff) / 10;
                int g = (res.GainQ[sf] + 8192) >> 14;
                if (g < -85)
                {
                    g = -85;
                }

                int negPart = (g >> 31) & g;
                int minOffset = -negPart;
                res.NrgRes[sf] = dec.DecodeCdf(cc.FcbgOffset(cfgSel, bucket, minOffset));
            }

            return res;
        }
    }
}
