using System;
using System.Text.Json;
using Unison.Voip.Engine.Crypto;
using Xunit;

namespace Unison.Core.Tests.Voip.Crypto
{
    public sealed class WarpTests
    {
        [Fact]
        public void Mi_tag_matches_kat()
        {
            JsonElement kat = CryptoKats.LoadSrtp();
            byte[] authKey = kat.HexOf("e2e_srtp", "peer_authKey");
            byte[] packet = kat.HexOf("inputs", "samplePacket");
            uint roc = kat.GetProperty("inputs").GetProperty("roc").GetUInt32();
            string wantTag = kat.Str("e2e_srtp", "warp_mi_tag4");

            byte[] tag = Warp.ComputeWarpMiTag(authKey, packet, roc, Warp.WarpMiTagLen);
            Assert.Equal(wantTag, CryptoKats.ToHex(tag));

            byte[] appended = Warp.AppendWarpMiTag(authKey, packet, roc, Warp.WarpMiTagLen);
            Assert.Equal(kat.Str("inputs", "samplePacket") + wantTag, CryptoKats.ToHex(appended));

            Assert.True(Warp.VerifyWarpMiTag(authKey, packet, roc, tag.Length, tag), "valid WARP MI tag was rejected");

            var tamperedTag = (byte[])tag.Clone();
            tamperedTag[0] ^= 0x80;
            Assert.False(Warp.VerifyWarpMiTag(authKey, packet, roc, tamperedTag.Length, tamperedTag), "tampered tag accepted");

            var wrongKey = (byte[])authKey.Clone();
            wrongKey[0] ^= 0x80;
            Assert.False(Warp.VerifyWarpMiTag(wrongKey, packet, roc, tag.Length, tag), "tag authenticated under wrong key");

            Assert.False(Warp.VerifyWarpMiTag(authKey, packet, roc + 1, tag.Length, tag), "tag authenticated under wrong ROC");
            Assert.False(Warp.VerifyWarpMiTag(authKey, packet, roc, 0, null), "empty tag accepted");

            var truncated = new byte[tag.Length - 1];
            Array.Copy(tag, truncated, truncated.Length);
            Assert.False(Warp.VerifyWarpMiTag(authKey, packet, roc, tag.Length, truncated), "truncated tag accepted");
            Assert.False(Warp.VerifyWarpMiTag(authKey, packet, roc, 21, new byte[21]), "oversized tag accepted");
        }

        [Fact]
        public void Audio_piggyback_extension_gating()
        {
            Assert.Null(Warp.AudioPiggybackExtensionFor(0, true, Warp.WarpPiggybackStartPacket));
            Assert.Null(Warp.AudioPiggybackExtensionFor(2, false, Warp.WarpPiggybackStartPacket));

            uint? w = Warp.AudioPiggybackExtensionFor(2, true, Warp.WarpPiggybackStartPacket);
            Assert.Equal(0x30010000u, w);
        }
    }
}
