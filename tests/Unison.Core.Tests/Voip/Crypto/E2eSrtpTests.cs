using System;
using System.Text.Json;
using Unison.Voip.Engine.Crypto;
using Xunit;

namespace Unison.Core.Tests.Voip.Crypto
{
    public sealed class E2eSrtpTests
    {
        private readonly JsonElement _kat = CryptoKats.LoadSrtp();

        private uint Ssrc => _kat.GetProperty("inputs").GetProperty("ssrc").GetUInt32();

        private ushort Seq => _kat.GetProperty("inputs").GetProperty("seq").GetUInt16();

        private uint Roc => _kat.GetProperty("inputs").GetProperty("roc").GetUInt32();

        [Fact]
        public void DeriveE2eKeys_matches_kat()
        {
            byte[] callKey = _kat.HexOf("inputs", "callKey");

            E2eSrtpKeys peer = E2eSrtp.DeriveE2eKeys(callKey, _kat.Str("inputs", "peerLid"));
            Assert.Equal(_kat.Str("e2e_srtp", "peer_cipherKey"), CryptoKats.ToHex(peer.CipherKey));
            Assert.Equal(_kat.Str("e2e_srtp", "peer_salt"), CryptoKats.ToHex(peer.Salt));
            Assert.Equal(_kat.Str("e2e_srtp", "peer_authKey"), CryptoKats.ToHex(peer.AuthKey));

            E2eSrtpKeys self = E2eSrtp.DeriveE2eKeys(callKey, _kat.Str("inputs", "selfLid"));
            Assert.Equal(_kat.Str("e2e_srtp", "self_cipherKey"), CryptoKats.ToHex(self.CipherKey));
            Assert.Equal(_kat.Str("e2e_srtp", "self_authKey"), CryptoKats.ToHex(self.AuthKey));
        }

        [Fact]
        public void BuildE2eRtpIv_matches_kat()
        {
            byte[] salt = _kat.HexOf("e2e_srtp", "peer_salt");

            byte[] iv = E2eSrtp.BuildE2eRtpIv(salt, Ssrc, Roc, Seq);

            Assert.Equal(_kat.Str("e2e_srtp", "rtpIv"), CryptoKats.ToHex(iv));
        }

        [Fact]
        public void CryptPayload_matches_kat_and_round_trips()
        {
            var keys = new E2eSrtpKeys(
                _kat.HexOf("e2e_srtp", "peer_cipherKey"),
                _kat.HexOf("e2e_srtp", "peer_salt"),
                new byte[20]);
            byte[] payload = _kat.HexOf("inputs", "payload");

            byte[] ct = E2eSrtp.CryptPayload(keys, Ssrc, Seq, Roc, payload);
            Assert.Equal(_kat.Str("e2e_srtp", "cipher_out"), CryptoKats.ToHex(ct));

            byte[] pt = E2eSrtp.CryptPayload(keys, Ssrc, Seq, Roc, ct);
            Assert.Equal(payload, pt);
        }

        [Fact]
        public void Srtcp_round_trips_and_authenticates()
        {
            var callKey = new byte[32];
            for (int i = 0; i < callKey.Length; i++)
            {
                callKey[i] = (byte)i;
            }

            E2eSrtpKeys keys = E2eSrtp.DeriveE2eSrtcpKeys(callKey, "111111111111111:0@lid");
            E2eSrtpKeys rtpKeys = E2eSrtp.DeriveE2eKeys(callKey, "111111111111111:0@lid");
            Assert.False(SameKeys(keys, rtpKeys), "SRTCP keys must use distinct KDF labels");

            uint ssrc = 0x12345678;
            byte[] plain = CryptoKats.Hex("80c8000612345678ea11180000000000000006400000000500000258");
            byte[] protectedPacket = E2eSrtp.ProtectSrtcp(keys, ssrc, 1, plain);
            Assert.Equal(plain.Length + E2eSrtp.SrtcpTrailerLen, protectedPacket.Length);

            byte[] recovered;
            uint index;
            Assert.True(E2eSrtp.TryUnprotectSrtcp(keys, ssrc, protectedPacket, out recovered, out index));
            Assert.Equal(1u, index);
            Assert.Equal(plain, recovered);

            protectedPacket[protectedPacket.Length - 1] ^= 1;
            Assert.False(E2eSrtp.TryUnprotectSrtcp(keys, ssrc, protectedPacket, out recovered, out index), "forged SRTCP tag accepted");
        }

        [Fact]
        public void DeriveE2eSrtcpKeysFromRaw_matches_raw_root_derivation()
        {
            var rawE2e = new byte[32];
            for (int i = 0; i < rawE2e.Length; i++)
            {
                rawE2e[i] = (byte)(0xa0 + i);
            }
            const string participantId = "111111111111111:14@lid";

            E2eSrtpKeys got = E2eSrtp.DeriveE2eSrtcpKeysFromRaw(rawE2e, participantId);
            Assert.Equal("96ec6c38976e1561a01929ef61627fd9", CryptoKats.ToHex(got.CipherKey));
            Assert.Equal("b033e3437b17edaa45863a19a6a3969633353bc7", CryptoKats.ToHex(got.AuthKey));
            Assert.Equal("d66012ec5832edc623a9fd742ff1", CryptoKats.ToHex(got.Salt));

            E2eSrtpKeys want = E2eSrtp.DeriveE2eSrtcpKeys(rawE2e, participantId);
            Assert.True(SameKeys(got, want));

            var shortRaw = new byte[31];
            Array.Copy(rawE2e, shortRaw, 31);
            Assert.Throws<ArgumentException>(() => E2eSrtp.DeriveE2eSrtcpKeysFromRaw(shortRaw, participantId));
        }

        [Fact]
        public void Short_key_material_is_rejected()
        {
            var shortKey = new byte[31];
            Assert.Throws<ArgumentException>(() => E2eSrtp.DeriveE2eKeys(shortKey, "x"));
            Assert.Throws<ArgumentException>(() => E2eSrtp.DeriveE2eSrtcpKeys(shortKey, "x"));
            Assert.Throws<ArgumentException>(() => E2eSrtp.DeriveE2eKeysFromRaw(shortKey, "x"));
        }

        [Fact]
        public void Roc_trackers_wrap()
        {
            var tx = new RocTracker();
            Assert.Equal(0u, tx.Advance(0xFFFE));
            tx.Advance(0xFFFF);
            Assert.Equal(1u, tx.Advance(0x0000));
            tx.Advance(0x0001);
            Assert.Equal(1u, tx.Advance(0x0000));
            tx.Advance(0x0001);
            foreach (ushort s in new ushort[] { 0x7000, 0xE000, 0xFFFF })
            {
                tx.Advance(s);
            }
            Assert.Equal(2u, tx.Advance(0x0000));

            var rx = new RecvRocTracker();
            Assert.Equal(0u, rx.GuessRoc(0xFFFE));
            rx.GuessRoc(0xFFFF);
            Assert.Equal(1u, rx.GuessRoc(0x0000));
            rx.GuessRoc(0x0001);
            Assert.Equal(1u, rx.GuessRoc(0x0000));
            Assert.Equal(1u, rx.GuessRoc(0x0002));
            foreach (ushort s in new ushort[] { 0x7000, 0xE000, 0xFFFF })
            {
                Assert.Equal(1u, rx.GuessRoc(s));
            }
            Assert.Equal(2u, rx.GuessRoc(0x0000));
            Assert.Equal(1u, rx.GuessRoc(0xFFF0));
            Assert.Equal(2u, rx.GuessRoc(0x0001));
        }

        [Fact]
        public void Recv_roc_estimate_commits_only_authenticated_packet()
        {
            var rx = new RecvRocTracker();
            Assert.Equal(0u, rx.EstimateRoc(0xFFFF));
            Assert.Equal(0u, rx.EstimateRoc(0x0000));

            uint roc = rx.EstimateRoc(0xFFFF);
            rx.CommitRoc(roc, 0xFFFF);
            Assert.Equal(1u, rx.EstimateRoc(0x0000));

            Assert.Equal(1u, rx.EstimateRoc(0x0001));
            Assert.Equal(1u, rx.EstimateRoc(0x0000));

            rx.CommitRoc(1, 0x0000);
            Assert.Equal(1u, rx.EstimateRoc(0x0001));
        }

        [Fact]
        public void Unauthenticated_staircase_cannot_advance_roc_without_commit()
        {
            var rx = new RecvRocTracker();
            rx.GuessRoc(0x7FFE);
            Assert.Equal(0u, rx.Roc);

            rx.EstimateRoc(0xFFFE);
            rx.EstimateRoc(0x7FFD);
            Assert.Equal(0u, rx.Roc);
            Assert.Equal(0u, rx.EstimateRoc(0x7FFF));

            uint roc = rx.EstimateRoc(0xFFFE);
            rx.CommitRoc(roc, 0xFFFE);
            roc = rx.EstimateRoc(0x7FFD);
            rx.CommitRoc(roc, 0x7FFD);
            Assert.Equal(1u, rx.Roc);
        }

        private static bool SameKeys(E2eSrtpKeys a, E2eSrtpKeys b)
        {
            return CryptoKats.ToHex(a.CipherKey) == CryptoKats.ToHex(b.CipherKey)
                && CryptoKats.ToHex(a.Salt) == CryptoKats.ToHex(b.Salt)
                && CryptoKats.ToHex(a.AuthKey) == CryptoKats.ToHex(b.AuthKey);
        }
    }
}
