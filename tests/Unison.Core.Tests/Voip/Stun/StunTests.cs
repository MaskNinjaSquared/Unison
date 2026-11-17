using System.Buffers.Binary;
using System.Text;
using Unison.Voip.Engine.Stun;

namespace Unison.Core.Tests.Voip.Stun
{
    // Ports: meowcaller stun/stun_test.go (MIT)
    public sealed class StunTests
    {
        private const string WasmStreamDescriptorsTemplateHex =
            "0a0618cabc85ae040a08100118a5acafae0a0a08100218d6a4e6f90f0a08080118f7dd9eb60a0a0a0801100118abccb1f30d" +
            "0a0a0801100218dadaef8a050a08080218c5e9ec8e0b0a0a0802100118fdc2b1b60f0a0a0802100218b097f7b209";

        private static StunKats K => StunKats.Instance;

        [Fact]
        public void Crc32_is_ieee()
        {
            uint got = StunCodec.StunFingerprint(Encoding.ASCII.GetBytes("abc"));
            Assert.Equal(K.Crc32Abc, got);
            Assert.Equal(0x352441c2u, got);
        }

        [Fact]
        public void Attr_endpoint_and_native_sender_sub_match_kat()
        {
            byte[] token = StunKats.Hex(K.RelayToken);
            Assert.Equal(K.AttrToken, StunKats.ToHex(StunCodec.StunAttr(StunCodec.AttrRelayToken, token)));

            byte[] ep;
            Assert.True(StunCodec.EncodeXorRelayEndpoint("157.240.226.133", 3478, out ep));
            Assert.Equal(K.XorEndpoint, StunKats.ToHex(ep));

            Assert.Equal(K.NativeSenderSub, StunKats.ToHex(StunCodec.CreateNativeSenderSubscription(K.Ssrc)));
        }

        [Fact]
        public void Encode_request_mi_and_fingerprint_match_kat()
        {
            byte[] attrs = StunCodec.StunAttr(StunCodec.AttrRelayToken, StunKats.Hex(K.RelayToken));
            byte[] miKey = StunKats.Hex(K.MiKey);

            byte[] minimal = StunCodec.EncodeStunRequest(StunCodec.MsgAllocateRequest, K.TxBytes, attrs, miKey, false);
            Assert.Equal(K.MinimalMi, StunKats.ToHex(minimal));

            byte[] withFp = StunCodec.EncodeStunRequest(StunCodec.MsgAllocateRequest, K.TxBytes, attrs, miKey, true);
            Assert.Equal(K.WithFp, StunKats.ToHex(withFp));
        }

        [Fact]
        public void Wasm_allocate_and_ping_match_kat()
        {
            byte[] ep;
            Assert.True(StunCodec.EncodeXorRelayEndpoint("157.240.226.133", 3478, out ep));

            byte[] alloc = WaStunAllocate.BuildWasmStunAllocateRequest(
                K.TxBytes, StunKats.Hex(K.RelayToken), ep, StunKats.Hex(K.MiKey));
            Assert.Equal(K.WasmAllocate, StunKats.ToHex(alloc));

            Assert.Equal(K.Ping, StunKats.ToHex(StunCodec.BuildWhatsappPing(K.TxBytes)));
        }

        [Fact]
        public void Wasm_stream_descriptors_match_captured_template()
        {
            uint[] ssrcs =
            {
                1170300490, 2781599269, 4281963094,
                2798104311, 3731645995, 1364979034,
                2983933125, 4140589437, 2522729392,
            };
            Assert.Equal(WasmStreamDescriptorsTemplateHex, StunKats.ToHex(WaStunAllocate.CreateWasmStreamDescriptors(ssrcs)));
        }

        [Fact]
        public void Wasm_group_allocate_carries_captured_video_subscriptions()
        {
            uint[] ssrcs =
            {
                0x3ea26c0c, 0x0bf99b28, 0xf42e4556,
                0x14e8f126, 0xbb16134f, 0x98b14f00,
                0xe0e04163, 0x74ed8516, 0xdea8a613,
            };
            byte[] transactionId = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
            byte[] endpointXor = { 0x2c, 0x84, 0xbc, 0xe2, 0xb5, 0xc7 };
            byte[] relayToken = { 0x01, 0x02, 0x03 };
            uint[] hbhFec = { 0xc1a17938, 0x1bb20c84 };
            byte[] miKey = Encoding.ASCII.GetBytes("0123456789abcdef");

            byte[] packet = WaStunAllocate.BuildWasmStunAllocateRequestWithGroupSubscriptionsAndHBHFEC(
                transactionId, relayToken, endpointXor, ssrcs, 0xb31ded3e, hbhFec, new uint[] { 1, 2 }, miKey);

            IList<StunAttribute> attrs = StunParse.ParseStunAttributes(packet);
            ushort[] wantTypes = { 0x4000, 0x4025, 0x4021, 0x4024, 0x805a, 0x0016, 0x0008 };
            Assert.Equal(wantTypes, attrs.Select(a => a.AttrType).ToArray());

            Assert.Equal(
                "0a1f0a1d0a0fa6e2a3a701cfa6d8d80b809ec5c5091204080110011204080210010a130a110a0fe38281870e968ab6a70793cca2f50d" +
                "0a1a0a180a0e8cd889f503a8b6e65fd68ab9a10f12020801120208020a110a0f0a05bedaf7980b1202080112020802",
                StunKats.ToHex(attrs[1].Value));
            Assert.Equal("1202080112020802", StunKats.ToHex(attrs[2].Value));
            Assert.Equal(
                "0a06188cd889f5030a07100118a8b6e65f0a08100218d68ab9a10f0a08080118a6e2a3a7010a0a0801100118cfa6d8d80b" +
                "0a0a0801100218809ec5c5090a08080218e38281870e0a0a0802100118968ab6a7070a0a080210021893cca2f50d" +
                "0a0a0803100318b8f2858d0c0a0a08041003188499c8dd01",
                StunKats.ToHex(attrs[3].Value));
            Assert.Equal("02", StunKats.ToHex(attrs[4].Value));

            byte[] oneParticipant = WaStunAllocate.BuildWasmStunAllocateRequestWithGroupSubscriptionsAndHBHFEC(
                transactionId, relayToken, endpointXor, ssrcs, 0xb31ded3e, hbhFec, new uint[] { 1 }, miKey);
            IList<StunAttribute> oneAttrs = StunParse.ParseStunAttributes(oneParticipant);
            Assert.Equal(WaStunAllocate.CreateWasmStreamDescriptors(ssrcs), oneAttrs[3].Value);
        }

        [Fact]
        public void Parse_round_trips_attributes()
        {
            byte[] minimal = StunKats.Hex(K.MinimalMi);
            Assert.True(StunParse.IsStunPacket(minimal));

            ushort messageType;
            Assert.True(StunParse.StunMessageType(minimal, out messageType));
            Assert.Equal(StunCodec.MsgAllocateRequest, messageType);

            IList<StunAttribute> attrs = StunParse.ParseStunAttributes(minimal);
            Assert.Equal(2, attrs.Count);
            Assert.Equal(StunCodec.AttrRelayToken, attrs[0].AttrType);
            Assert.Equal(K.RelayToken, StunKats.ToHex(attrs[0].Value));
            Assert.Equal(StunCodec.AttrMessageIntegrity, attrs[1].AttrType);
            Assert.Equal(20, attrs[1].Value.Length);
        }

        [Fact]
        public void Protobuf_payloads_match_kat()
        {
            Assert.Equal(K.VoipSenderSubscriptions, StunKats.ToHex(WaStunAllocate.CreateVoipSenderSubscriptions(K.Ssrc)));
            Assert.Equal(K.ApkSenderSubscriptionsNoPid, StunKats.ToHex(WaStunAllocate.CreateApkSenderSubscriptions(K.Ssrc, null)));
            Assert.Equal(K.ApkSenderSubscriptionsPid, StunKats.ToHex(WaStunAllocate.CreateApkSenderSubscriptions(K.Ssrc, 7u)));
            Assert.Equal(K.ApkStreamDescriptors, StunKats.ToHex(WaStunAllocate.CreateApkStreamDescriptors(K.Ssrc)));
        }

        [Fact]
        public void Android_allocate_carries_four_attrs()
        {
            byte[] pkt = WaStunAllocate.BuildAndroidStunAllocateRequest(
                K.TxBytes, StunKats.Hex(K.RelayToken), K.Ssrc, null, StunKats.Hex(K.MiKey), false);

            IList<StunAttribute> attrs = StunParse.ParseStunAttributes(pkt);
            ushort[] want =
            {
                StunCodec.AttrRelayToken, StunCodec.AttrSenderSubscriptionsV2,
                StunCodec.AttrStreamDescriptors, StunCodec.AttrMessageIntegrity,
            };
            Assert.Equal(want, attrs.Select(a => a.AttrType).ToArray());
            Assert.Equal(WaStunAllocate.CreateApkStreamDescriptors(K.Ssrc), attrs[2].Value);
        }

        [Fact]
        public void Pong_matching()
        {
            byte[] tx = K.TxBytes;
            byte[] pong = StunCodec.BuildWhatsappPing(tx);
            BinaryPrimitives.WriteUInt16BigEndian(pong.AsSpan(0, 2), StunCodec.MsgWhatsappPong);

            Assert.True(StunParse.IsWhatsappPong(pong, tx));
            Assert.True(StunParse.IsWhatsappPong(pong, null));
            Assert.False(StunParse.IsWhatsappPong(pong, new byte[12]));
        }

        // Beyond stun_test.go: C# shape guards and parser branches the Go tests leave implicit.

        [Theory]
        [InlineData(11)]
        [InlineData(13)]
        public void Transaction_id_must_be_12_bytes(int length)
        {
            Assert.Throws<ArgumentException>(() => StunCodec.BuildWhatsappPing(new byte[length]));
            Assert.Throws<ArgumentException>(() =>
                StunCodec.EncodeStunRequest(StunCodec.MsgAllocateRequest, new byte[length], new byte[0], null, false));
        }

        [Fact]
        public void Stream_ssrcs_must_be_nine_words()
        {
            Assert.Throws<ArgumentException>(() => WaStunAllocate.CreateWasmStreamDescriptors(new uint[8]));
        }

        [Theory]
        [InlineData("1.2.3")]
        [InlineData("1.2.3.4.5")]
        [InlineData("1.2.3.256")]
        [InlineData("")]
        [InlineData(null)]
        public void Xor_endpoint_rejects_malformed_ipv4(string? ipv4)
        {
            byte[] ep;
            Assert.False(StunCodec.EncodeXorRelayEndpoint(ipv4!, 3478, out ep));
            Assert.Equal(new byte[6], ep);
        }

        [Fact]
        public void Xor_endpoint_skips_unparsable_octets_like_go()
        {
            byte[] expected;
            byte[] got;
            Assert.True(StunCodec.EncodeXorRelayEndpoint("157.240.226.133", 3478, out expected));
            Assert.True(StunCodec.EncodeXorRelayEndpoint("157.x.240.226.133", 3478, out got));
            Assert.Equal(expected, got);
        }

        [Fact]
        public void Allocate_success_and_error_classification()
        {
            byte[] success = StunCodec.EncodeStunRequest(StunCodec.MsgAllocateSuccess, K.TxBytes, new byte[0], null, false);
            byte[] binding = StunCodec.EncodeStunRequest(StunCodec.MsgBindingSuccess, K.TxBytes, new byte[0], null, false);
            Assert.True(StunParse.IsAllocateOrBindingSuccess(success));
            Assert.True(StunParse.IsAllocateOrBindingSuccess(binding));
            Assert.False(StunParse.IsAllocateError(success));
            Assert.False(StunParse.IsAllocateOrBindingSuccess(new byte[] { 0x01, 0x03 }));

            byte[] errorValue = { 0x00, 0x00, 0x04, 0x01, (byte)'x' };
            byte[] error = StunCodec.EncodeStunRequest(
                StunCodec.MsgAllocateError, K.TxBytes, StunCodec.StunAttr(StunCodec.AttrErrorCode, errorValue), null, false);
            Assert.True(StunParse.IsAllocateError(error));

            ushort code;
            Assert.True(StunParse.ParseStunErrorCode(error, out code));
            Assert.Equal((ushort)401, code);
            Assert.False(StunParse.ParseStunErrorCode(success, out code));
        }

        [Fact]
        public void Transaction_id_extraction()
        {
            byte[] tx;
            Assert.True(StunParse.StunTransactionID(StunKats.Hex(K.Ping), out tx));
            Assert.Equal(K.Tx, StunKats.ToHex(tx));
            Assert.False(StunParse.StunTransactionID(new byte[19], out tx));
            Assert.Null(tx);
        }

        [Fact]
        public void Parse_rejects_non_stun()
        {
            Assert.Empty(StunParse.ParseStunAttributes(new byte[] { 0x80, 0x00 }));
            Assert.Empty(StunParse.ParseStunAttributes(null!));
        }
    }
}
