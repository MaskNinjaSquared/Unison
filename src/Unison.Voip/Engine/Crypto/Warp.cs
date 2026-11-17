// =============================================================================
// Warp
//
// WARP RTP extension constants and the WARP MESSAGE-INTEGRITY tag
// (truncated HMAC-SHA1 appended to protected packets).
// Ports: meowcaller srtp/warp.go (MIT)
// =============================================================================
using System;
using System.Security.Cryptography;

namespace Unison.Voip.Engine.Crypto
{
    public static class Warp
    {
        public const ushort WarpExtProfile = 0xdebe;
        public const int WarpMiTagLen = 4;
        public const int WarpPiggybackStartPacket = 2;

        private const int Sha1Size = 20;
        private const uint WarpAudioPiggybackWord = 0x30010000;

        /// <summary>Audio piggyback extension word as big-endian bytes (fresh copy).</summary>
        public static byte[] WarpAudioPiggybackExt
        {
            get { return new byte[] { 0x30, 0x01, 0x00, 0x00 }; }
        }

        /// <summary>Extension word for packetIndex, or null for the first packets / when disabled.</summary>
        public static uint? AudioPiggybackExtensionFor(int packetIndex, bool enabled, int startPacket)
        {
            if (!enabled || packetIndex < startPacket)
            {
                return null;
            }

            return WarpAudioPiggybackWord;
        }

        /// <summary>First tagLen bytes of HMAC-SHA1(authKey, packetWithoutTag || roc_be32).</summary>
        public static byte[] ComputeWarpMiTag(byte[] authKey, byte[] packetWithoutTag, uint roc, int tagLen)
        {
            if (tagLen < 0 || tagLen > Sha1Size)
            {
                throw new ArgumentException("warp mi tag length must be between 0 and " + Sha1Size, nameof(tagLen));
            }

            var rocBe = new byte[] { (byte)(roc >> 24), (byte)(roc >> 16), (byte)(roc >> 8), (byte)roc };
            byte[] mac;
            using (var hmac = new HMACSHA1(authKey))
            {
                hmac.TransformBlock(packetWithoutTag, 0, packetWithoutTag.Length, null, 0);
                hmac.TransformFinalBlock(rocBe, 0, rocBe.Length);
                mac = hmac.Hash;
            }

            var tag = new byte[tagLen];
            Buffer.BlockCopy(mac, 0, tag, 0, tagLen);
            return tag;
        }

        public static byte[] AppendWarpMiTag(byte[] authKey, byte[] packetWithoutTag, uint roc, int tagLen)
        {
            byte[] tag = ComputeWarpMiTag(authKey, packetWithoutTag, roc, tagLen);
            var output = new byte[packetWithoutTag.Length + tag.Length];
            Buffer.BlockCopy(packetWithoutTag, 0, output, 0, packetWithoutTag.Length);
            Buffer.BlockCopy(tag, 0, output, packetWithoutTag.Length, tag.Length);
            return output;
        }

        /// <summary>Authenticates a protected WARP packet and its rollover counter (constant time).</summary>
        public static bool VerifyWarpMiTag(byte[] authKey, byte[] packetWithoutTag, uint roc, int tagLen, byte[] receivedTag)
        {
            if (tagLen <= 0 || tagLen > Sha1Size || receivedTag == null || receivedTag.Length != tagLen)
            {
                return false;
            }

            byte[] expected = ComputeWarpMiTag(authKey, packetWithoutTag, roc, tagLen);
            return ConstantTime.Equals(expected, 0, receivedTag, 0, tagLen);
        }
    }
}
