using System.Text.Json;
using Unison.Voip.Engine.Crypto;
using Xunit;

namespace Unison.Core.Tests.Voip.Crypto
{
    public sealed class HbhSrtpTests
    {
        private readonly JsonElement _kat = CryptoKats.LoadSrtp();

        [Fact]
        public void Uplink_derivation_matches_kat()
        {
            byte[] hbh = _kat.HexOf("inputs", "hbhKey");

            byte[] uplink = HbhSrtp.DeriveHbhSrtpKeyUplink(hbh);
            Assert.Equal(_kat.Str("hbh_srtp", "uplinkKey30"), CryptoKats.ToHex(uplink));

            SrtpKeyingMaterial keying = HbhSrtp.KeyingFromHbhKeyUplink(hbh);
            Assert.Equal(_kat.Str("hbh_srtp", "masterKey"), CryptoKats.ToHex(keying.MasterKey));
            Assert.Equal(_kat.Str("hbh_srtp", "masterSalt"), CryptoKats.ToHex(keying.MasterSalt));

            LibsrtpSessionKeys session = HbhSrtp.ExpandLibsrtpSessionKeys(keying);
            Assert.Equal(_kat.Str("hbh_srtp", "sessionKey"), CryptoKats.ToHex(session.SessionKey));
            Assert.Equal(_kat.Str("hbh_srtp", "sessionSalt"), CryptoKats.ToHex(session.SessionSalt));
            Assert.Equal(_kat.Str("hbh_srtp", "authKey"), CryptoKats.ToHex(session.AuthKey));
        }

        [Fact]
        public void Icm_nonce_and_cipher_match_kat()
        {
            byte[] hbh = _kat.HexOf("inputs", "hbhKey");
            JsonElement inputs = _kat.GetProperty("inputs");
            uint ssrc = inputs.GetProperty("ssrc").GetUInt32();
            ulong packetIndex = ((ulong)inputs.GetProperty("roc").GetUInt32() << 16) | inputs.GetProperty("seq").GetUInt16();

            LibsrtpSessionKeys session = HbhSrtp.ExpandLibsrtpSessionKeys(HbhSrtp.KeyingFromHbhKeyUplink(hbh));

            byte[] nonce = HbhSrtp.BuildRtpIcmNonce(ssrc, packetIndex);
            Assert.Equal(_kat.Str("hbh_srtp", "rtpIcmNonce"), CryptoKats.ToHex(nonce));

            byte[] payload = _kat.HexOf("inputs", "payload");
            byte[] ct = HbhSrtp.CryptRtpPayload(session, ssrc, packetIndex, payload);
            Assert.Equal(_kat.Str("hbh_srtp", "cipher_out"), CryptoKats.ToHex(ct));

            byte[] pt = HbhSrtp.CryptRtpPayload(session, ssrc, packetIndex, ct);
            Assert.Equal(payload, pt);
        }

        [Fact]
        public void Hbh_key_must_be_exactly_30_bytes()
        {
            Assert.Throws<System.ArgumentException>(() => HbhSrtp.DeriveHbhSrtpKeyUplink(new byte[29]));
            Assert.Throws<System.ArgumentException>(() => HbhSrtp.DeriveHbhSrtpKeyDownlink(new byte[31]));
        }
    }
}
