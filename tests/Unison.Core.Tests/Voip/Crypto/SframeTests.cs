using System;
using System.Text.Json;
using Unison.Voip.Engine.Crypto;
using Xunit;

namespace Unison.Core.Tests.Voip.Crypto
{
    public sealed class SframeTests
    {
        private readonly JsonElement _kat = CryptoKats.LoadSrtp();

        private byte[] CallKey => _kat.HexOf("inputs", "callKey");

        private string SelfLid => _kat.Str("inputs", "selfLid");

        private string PeerLid => _kat.Str("inputs", "peerLid");

        [Fact]
        public void Participant_key_and_label_match_kat()
        {
            string peerId = Sframe.FormatSframeParticipantId(PeerLid);
            Assert.Equal(_kat.Str("sframe", "participantPeerId"), peerId);
            Assert.Equal(_kat.Str("sframe", "infoLabelPeer"), Sframe.SframeInfoLabel(peerId));

            byte[] key = Sframe.DeriveE2eSframeKeyForParticipant(CallKey, peerId);
            Assert.Equal(_kat.Str("sframe", "peerKey32"), CryptoKats.ToHex(key));
        }

        [Fact]
        public void DeriveWarpAuthKey_matches_vector_and_rejects_short_key()
        {
            const string wantWarpAuthKey = "6c768f61c7bd16f8c022b58d6a9f01ab832abe95e27538a7d6c584a26b8e0734";
            Assert.Equal(wantWarpAuthKey, CryptoKats.ToHex(Sframe.DeriveWarpAuthKey(CallKey)));

            var shortKey = new byte[31];
            Array.Copy(CallKey, shortKey, 31);
            Assert.Throws<ArgumentException>(() => Sframe.DeriveWarpAuthKey(shortKey));
        }

        [Fact]
        public void Counter_iv_and_header_match_kat()
        {
            Assert.Equal(_kat.Str("sframe", "counterToIv_5"), CryptoKats.ToHex(Sframe.CounterToIv(5)));
            Assert.Equal(_kat.Str("sframe", "header_5_0"), CryptoKats.ToHex(Sframe.BuildSframeHeader(5, 0)));

            ulong counter;
            ulong keyId;
            Assert.True(Sframe.TryParseSframeHeader(Sframe.BuildSframeHeader(5, 0), out counter, out keyId));
            Assert.Equal(5ul, counter);
            Assert.Equal(0ul, keyId);
        }

        [Fact]
        public void Encrypt_matches_kat()
        {
            var session = new SframeSession(CallKey, SelfLid, PeerLid);
            session.TxCounter = 5;

            byte[] output = session.Encrypt(_kat.HexOf("inputs", "payload"));

            Assert.Equal(_kat.Str("sframe", "encrypt_out"), CryptoKats.ToHex(output));
        }

        [Fact]
        public void Encrypt_decrypt_round_trips()
        {
            var sender = new SframeSession(CallKey, SelfLid, PeerLid);
            var receiver = new SframeSession(CallKey, PeerLid, SelfLid);
            byte[] payload = System.Text.Encoding.ASCII.GetBytes("hello sframe payload");

            byte[] frame = sender.Encrypt(payload);

            byte[] plain;
            Assert.True(receiver.TryDecrypt(frame, out plain));
            Assert.Equal(payload, plain);
        }

        [Fact]
        public void Wrong_key_does_not_forge()
        {
            var sender = new SframeSession(CallKey, SelfLid, PeerLid);
            var payload = new byte[24];
            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] = 0xaa;
            }
            byte[] frame = sender.Encrypt(payload);

            byte[] other = CallKey;
            other[0] ^= 0xff;
            var receiver = new SframeSession(other, PeerLid, SelfLid);

            byte[] plain;
            Assert.False(receiver.TryDecrypt(frame, out plain));
            Assert.Null(plain);
        }

        [Fact]
        public void Plain_opus_passes_through()
        {
            var receiver = new SframeSession(CallKey, SelfLid, PeerLid);
            var frames = new[]
            {
                new byte[] { 0x00 },
                CryptoKats.Hex("90b81414c4"),
                CryptoKats.Hex("12101a759d3399bbaefb874fd75a004af7c0"),
                CryptoKats.Hex("9036ba6ffa40"),
                CryptoKats.Hex("1236262b4ac920b1206166637b5af2"),
            };

            foreach (byte[] f in frames)
            {
                byte[] plain;
                Assert.False(receiver.TryDecrypt(f, out plain), "plain frame " + CryptoKats.ToHex(f) + " classified as decrypted");
            }
        }

        [Fact]
        public void Session_rejects_call_key_that_is_not_32_bytes()
        {
            Assert.Throws<ArgumentException>(() => new SframeSession(new byte[31], SelfLid, PeerLid));
        }
    }
}
