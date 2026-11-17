// =============================================================================
// SplitRed
//
// MLow RED ("SplitRed") depacketization, the outermost wire layer of a WhatsApp
// MLow RTP audio payload. Only valid when the call negotiated redundancy > 0;
// a bare frame's high-bit-set TOC would misparse.
// Ports: meowcaller mlow/red.go (MIT)
// =============================================================================
using System;
using System.Collections.Generic;

namespace Unison.Voip.Engine.Mlow
{
    public enum SplitRedError
    {
        None = 0,
        PktSizeZero,
        HeaderTooShort,
        RedundantTooShort,
        MainTooShort,
    }

    /// <summary>One frame extracted from a SplitRed payload (TOC + body) plus RED metadata.</summary>
    public sealed class MlowFrame
    {
        public MlowFrame(byte[] data, byte timeCode, bool isMain)
        {
            Data = data;
            TimeCode = timeCode;
            IsMain = isMain;
        }

        public byte[] Data { get; }

        public byte TimeCode { get; }

        public bool IsMain { get; }
    }

    public static class SplitRed
    {
        /// <summary>
        /// Parses a SplitRed packet into its frames: redundant blocks in header order,
        /// then the main frame last.
        /// </summary>
        public static bool TryDepack(byte[] p, out MlowFrame[] frames, out SplitRedError error)
        {
            frames = null;
            int n = p == null ? 0 : p.Length;
            if (n == 0)
            {
                error = SplitRedError.PktSizeZero;
                return false;
            }

            var codes = new List<byte>();
            var sizes = new List<int>();
            int cur = 0;
            int rem = n;
            while (true)
            {
                if (rem == 0)
                {
                    error = SplitRedError.HeaderTooShort;
                    return false;
                }

                byte b0 = p[cur];
                if (b0 < 0x80)
                {
                    if (rem <= 1)
                    {
                        error = SplitRedError.MainTooShort;
                        return false;
                    }

                    break;
                }

                if (rem <= 2)
                {
                    error = SplitRedError.RedundantTooShort;
                    return false;
                }

                int size = p[cur + 1];
                if (size + 2 >= rem)
                {
                    error = SplitRedError.RedundantTooShort;
                    return false;
                }

                codes.Add((byte)(b0 & 0x7f));
                sizes.Add(size);
                cur += 2;
                rem -= size + 2;
            }

            byte mainCode = (byte)(p[cur] & 0x7f);
            cur++;

            frames = new MlowFrame[codes.Count + 1];
            for (int i = 0; i < codes.Count; i++)
            {
                frames[i] = new MlowFrame(Slice(p, cur, sizes[i]), codes[i], false);
                cur += sizes[i];
            }

            int mainSize = rem - 1;
            frames[codes.Count] = new MlowFrame(Slice(p, cur, mainSize), mainCode, true);
            error = SplitRedError.None;
            return true;
        }

        private static byte[] Slice(byte[] p, int offset, int count)
        {
            var o = new byte[count];
            Buffer.BlockCopy(p, offset, o, 0, count);
            return o;
        }
    }
}
