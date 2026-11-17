// =============================================================================
// SmplToc
//
// Decoded first byte of an inbound MLow frame: how to interpret the body, or
// that it is a standard Opus packet to route elsewhere.
// Ports: meowcaller mlow/toc.go (MIT)
// =============================================================================
namespace Unison.Voip.Engine.Mlow
{
    public struct SmplToc
    {
        private static readonly int[] SilkFrameMs = { 10, 20, 40, 60 };
        private static readonly int[] HybridFrameMs = { 10, 20 };
        private static readonly int[] SmplFrameMs = { 10, 20, 60, 120 };

        public bool StdOpus { get; private set; }

        public bool Sid { get; private set; }

        public bool Vad { get; private set; }

        public int SampleRate { get; private set; }

        public int FrameMs { get; private set; }

        public bool Voiced { get; private set; }

        public bool Active { get; private set; }

        public bool Flag2 { get; private set; }

        public bool Flag0 { get; private set; }

        public static SmplToc Parse(byte b)
        {
            if ((b & 0xC0) == 0xC0)
            {
                return new SmplToc { StdOpus = true, SampleRate = 16000, FrameMs = StandardOpusFrameMs(b) };
            }

            bool bit1 = ((b >> 1) & 1) != 0;
            bool vad = ((b >> 6) & 1) != 0;
            return new SmplToc
            {
                Sid = (b >> 7) != 0,
                Vad = vad,
                SampleRate = (b & 0x20) != 0 ? 32000 : 16000,
                FrameMs = SmplFrameMs[(b >> 3) & 3],
                Voiced = vad && bit1,
                Active = vad || bit1,
                Flag2 = ((b >> 2) & 1) != 0,
                Flag0 = (b & 1) != 0,
            };
        }

        /// <summary>RFC 6716 Table 2 frame duration (ms) from config b&gt;&gt;3; 2.5 ms rounds up to 3.</summary>
        internal static int StandardOpusFrameMs(byte b)
        {
            int config = b >> 3;
            if (config < 12)
            {
                return SilkFrameMs[config & 3];
            }

            if (config < 16)
            {
                return HybridFrameMs[(config - 12) & 1];
            }

            switch (config & 3)
            {
                case 0:
                    return 3;
                case 1:
                    return 5;
                case 2:
                    return 10;
                default:
                    return 20;
            }
        }
    }
}
