using Unison.Voip.Engine.Crypto;
using Unison.Voip.Engine.Media;
using Unison.Voip.Engine.Rtp;
using Xunit;

namespace Unison.Core.Tests.Voip.Media
{
    public sealed class MediaPipelineTests
    {
        private const string SelfLid = "111111111111111:0@lid";
        private const string PeerLid = "222222222222222:0@lid";
        private const uint Ssrc = 0x12345678;

        [Fact]
        public void Protect_then_unprotect_round_trips()
        {
            const string lid = "222222222222222:0@lid";
            var tx = new MediaPipeline(Iota32(), lid, lid, Ssrc, 960);
            var rx = new MediaPipeline(Iota32(), lid, lid, Ssrc, 960);
            byte[] opus = { 0x48, 0x11, 0x22, 0x33, 0x44, 0x55 };

            byte[] packet = tx.ProtectAudio(opus);
            RtpHeader header;
            byte[] payload;

            Assert.True(rx.TryUnprotectAudio(packet, out header, out payload));
            Assert.Equal((ushort)1, header.SequenceNumber);
            Assert.Equal(Ssrc, header.Ssrc);
            Assert.Equal(RtpPacket.RtpPayloadTypeOpus, header.PayloadType);
            Assert.Equal(opus, payload);
        }

        [Fact]
        public void Protect_encrypts_under_self_lid()
        {
            var pipe = new MediaPipeline(Iota32(), SelfLid, PeerLid, Ssrc, 960);
            byte[] opus = { 0x10, 0x21, 0x32, 0x43 };

            byte[] packet = pipe.ProtectAudio(opus);
            byte[] withoutTag = new byte[packet.Length - Warp.WarpMiTagLen];
            System.Buffer.BlockCopy(packet, 0, withoutTag, 0, withoutTag.Length);
            int headerLen;
            Assert.True(RtpPacket.TryGetRtpHeaderByteLength(withoutTag, out headerLen));
            byte[] body = new byte[withoutTag.Length - headerLen];
            System.Buffer.BlockCopy(withoutTag, headerLen, body, 0, body.Length);

            E2eSrtpKeys selfKeys = E2eSrtp.DeriveE2eKeys(Iota32(), SelfLid);
            E2eSrtpKeys peerKeys = E2eSrtp.DeriveE2eKeys(Iota32(), PeerLid);
            Assert.Equal(E2eSrtp.CryptPayload(selfKeys, Ssrc, 1, 0, opus), body);
            Assert.NotEqual(E2eSrtp.CryptPayload(peerKeys, Ssrc, 1, 0, opus), body);
        }

        [Fact]
        public void Unprotect_uses_peer_lid_and_rejects_tampering_without_poisoning_roc()
        {
            byte[] opus = { 0x48, 0x01, 0x02, 0x03, 0x04, 0x05 };
            var peerTx = new MediaPipeline(Iota32(), PeerLid, SelfLid, Ssrc, 960);
            byte[] fromPeer = peerTx.ProtectAudio(opus);
            RtpHeader header;
            byte[] recovered;

            var us = new MediaPipeline(Iota32(), SelfLid, PeerLid, Ssrc, 960);
            Assert.True(us.TryUnprotectAudio(fromPeer, out header, out recovered));
            Assert.Equal(opus, recovered);

            byte[] selfKeyed = new MediaPipeline(Iota32(), SelfLid, PeerLid, Ssrc, 960).ProtectAudio(opus);
            var us2 = new MediaPipeline(Iota32(), SelfLid, PeerLid, Ssrc, 960);
            Assert.False(us2.TryUnprotectAudio(selfKeyed, out header, out recovered));

            byte[] tampered = (byte[])fromPeer.Clone();
            tampered[tampered.Length - 1] ^= 0x80;
            var us3 = new MediaPipeline(Iota32(), SelfLid, PeerLid, Ssrc, 960);
            Assert.False(us3.TryUnprotectAudio(tampered, out header, out recovered));
            Assert.True(us3.TryUnprotectAudio(fromPeer, out header, out recovered));
            Assert.Equal(opus, recovered);
        }

        [Fact]
        public void Sequence_and_timestamp_advance_per_frame()
        {
            var tx = new MediaPipeline(Iota32(), SelfLid, PeerLid, Ssrc, 960);
            var rx = new MediaPipeline(Iota32(), PeerLid, SelfLid, Ssrc, 960);
            tx.ProtectAudio(new byte[] { 0x48, 1 });
            byte[] second = tx.ProtectAudio(new byte[] { 0x48, 2 });
            RtpHeader header;
            byte[] payload;

            Assert.True(rx.TryUnprotectAudio(second, out header, out payload));
            Assert.Equal((ushort)2, header.SequenceNumber);
            Assert.Equal(960u, header.Timestamp);
        }

        [Fact]
        public void Unprotect_rejects_short_packet()
        {
            var rx = new MediaPipeline(Iota32(), SelfLid, PeerLid, Ssrc, 960);
            RtpHeader header;
            byte[] payload;

            Assert.False(rx.TryUnprotectAudio(new byte[8], out header, out payload));
            Assert.False(rx.TryUnprotectAudio(null, out header, out payload));
        }

        private static byte[] Iota32()
        {
            var key = new byte[32];
            for (int i = 0; i < key.Length; i++)
            {
                key[i] = (byte)i;
            }

            return key;
        }
    }
}
