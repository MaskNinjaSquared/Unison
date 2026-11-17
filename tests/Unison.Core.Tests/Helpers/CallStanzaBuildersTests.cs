using System.Collections.Generic;
using Unison.Baileys.Protocol;
using Unison.Socket.UseCases.Calls;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class CallStanzaBuildersTests
    {
        [Fact]
        public void BuildPreaccept_matches_meowcaller_sendPreaccept_order()
        {
            BinaryNode node = CallStanzaBuilders.BuildPreaccept(
                "ABC",
                "1555@s.whatsapp.net",
                "1555@s.whatsapp.net",
                stanzaId: "PRE1",
                meJid: "me@s.whatsapp.net");

            Assert.Equal("call", node.Tag);
            Assert.Equal("PRE1", node.GetAttribute("id"));
            Assert.Equal("1555@s.whatsapp.net", node.GetAttribute("to"));

            var children = node.Content as List<BinaryNode>;
            Assert.NotNull(children);
            Assert.Equal("preaccept", children[0].Tag);

            var body = children[0].Content as List<BinaryNode>;
            Assert.NotNull(body);
            Assert.Equal(3, body.Count);
            Assert.Equal("audio", body[0].Tag);
            Assert.Equal("16000", body[0].GetAttribute("rate"));
            Assert.Equal("encopt", body[1].Tag);
            Assert.Equal("capability", body[2].Tag);
            Assert.Equal(
                new byte[] { 0x01, 0x05, 0xf7, 0x09, 0xe0, 0xbb, 0x07 },
                body[2].Content as byte[]);
        }

        [Fact]
        public void BuildAccept_orders_audio_video_net_encopt_metadata_like_meowcaller()
        {
            BinaryNode node = CallStanzaBuilders.BuildAccept(
                "ABC",
                "1555@s.whatsapp.net",
                "1555@s.whatsapp.net",
                stanzaId: "ACC1",
                isVideo: true);

            var children = node.Content as List<BinaryNode>;
            Assert.NotNull(children);
            Assert.Equal("accept", children[0].Tag);

            var body = children[0].Content as List<BinaryNode>;
            Assert.NotNull(body);
            Assert.Equal("audio", body[0].Tag);
            Assert.Equal("video", body[1].Tag);
            Assert.Equal("H264", body[1].GetAttribute("dec"));
            Assert.Equal("0", body[1].GetAttribute("device_orientation"));
            Assert.Equal("net", body[2].Tag);
            Assert.Equal("2", body[2].GetAttribute("medium"));
            Assert.Equal("encopt", body[3].Tag);
            Assert.Equal("metadata", body[4].Tag);
        }

        [Fact]
        public void BuildReject_matches_baileys_companion_shape()
        {
            BinaryNode node = CallStanzaBuilders.BuildReject(
                "ABC",
                "1555@s.whatsapp.net",
                "1555@s.whatsapp.net",
                meJid: "15550001111:1@s.whatsapp.net",
                stanzaId: "3EB0DEADBEEF");

            Assert.Equal("call", node.Tag);
            Assert.Equal("1555@s.whatsapp.net", node.GetAttribute("to"));
            Assert.Equal("15550001111:1@s.whatsapp.net", node.GetAttribute("from"));
            Assert.Equal("3EB0DEADBEEF", node.GetAttribute("id"));

            var children = node.Content as List<BinaryNode>;
            Assert.NotNull(children);
            Assert.Single(children);
            Assert.Equal("reject", children[0].Tag);
            Assert.Equal("ABC", children[0].GetAttribute("call-id"));
            Assert.Equal("1555@s.whatsapp.net", children[0].GetAttribute("call-creator"));
            Assert.Equal("0", children[0].GetAttribute("count"));
        }

        [Fact]
        public void BuildRelayLatency_echoes_probe_like_meowcaller_callee()
        {
            BinaryNode node = CallStanzaBuilders.BuildRelayLatency(
                "ABC",
                "peer:3@lid",
                "peer@lid",
                latencyMs: 42,
                relayName: "gru1",
                addressBytes: new byte[] { 1, 2, 3, 4, 5, 6 },
                stanzaId: "RL1");

            Assert.Equal("call", node.Tag);
            Assert.Equal("peer:3@lid", node.GetAttribute("to"));
            Assert.Equal("RL1", node.GetAttribute("id"));
            Assert.Null(node.GetAttribute("from"));

            var children = node.Content as List<BinaryNode>;
            Assert.NotNull(children);
            BinaryNode action = children[0];
            Assert.Equal("relaylatency", action.Tag);
            Assert.Equal("ABC", action.GetAttribute("call-id"));
            Assert.Equal("peer@lid", action.GetAttribute("call-creator"));

            var body = action.Content as List<BinaryNode>;
            Assert.NotNull(body);
            Assert.Single(body);
            Assert.Equal("te", body[0].Tag);
            Assert.Equal((0x02000000 + 42).ToString(), body[0].GetAttribute("latency"));
            Assert.Equal("gru1", body[0].GetAttribute("relay_name"));
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, body[0].Content as byte[]);
        }

        [Fact]
        public void BuildOffer_single_device_orders_like_meowcaller()
        {
            var keys = new List<CallStanzaBuilders.OfferDeviceKey>
            {
                new CallStanzaBuilders.OfferDeviceKey
                {
                    DeviceJid = "1555:0@s.whatsapp.net",
                    EncType = "pkmsg",
                    Ciphertext = new byte[] { 0xAA, 0xBB }
                }
            };

            BinaryNode node = CallStanzaBuilders.BuildOffer(
                "CALL1",
                "1555@lid",
                "me@lid",
                stanzaId: "OFF1",
                keys,
                privacyToken: new byte[] { 1, 2 },
                deviceIdentity: new byte[] { 9 });

            Assert.Equal("call", node.Tag);
            Assert.Equal("OFF1", node.GetAttribute("id"));
            Assert.Equal("1555@lid", node.GetAttribute("to"));
            Assert.Null(node.GetAttribute("from"));

            var children = node.Content as List<BinaryNode>;
            Assert.NotNull(children);
            Assert.Equal("offer", children[0].Tag);

            var body = children[0].Content as List<BinaryNode>;
            Assert.NotNull(body);
            Assert.Equal("privacy", body[0].Tag);
            Assert.Equal("audio", body[1].Tag);
            Assert.Equal("8000", body[1].GetAttribute("rate"));
            Assert.Equal("audio", body[2].Tag);
            Assert.Equal("16000", body[2].GetAttribute("rate"));
            Assert.Equal("net", body[3].Tag);
            Assert.Equal("3", body[3].GetAttribute("medium"));
            Assert.Equal("capability", body[4].Tag);
            Assert.Equal("enc", body[5].Tag);
            Assert.Equal("pkmsg", body[5].GetAttribute("type"));
            Assert.Equal("encopt", body[6].Tag);
            Assert.Equal("device-identity", body[7].Tag);
        }

        [Fact]
        public void BuildOffer_multi_device_uses_destination()
        {
            var keys = new List<CallStanzaBuilders.OfferDeviceKey>
            {
                new CallStanzaBuilders.OfferDeviceKey
                {
                    DeviceJid = "1555:0@s.whatsapp.net",
                    EncType = "msg",
                    Ciphertext = new byte[] { 1 }
                },
                new CallStanzaBuilders.OfferDeviceKey
                {
                    DeviceJid = "1555:1@s.whatsapp.net",
                    EncType = "msg",
                    Ciphertext = new byte[] { 2 }
                }
            };

            BinaryNode node = CallStanzaBuilders.BuildOffer(
                "CALL2",
                "1555@s.whatsapp.net",
                "me@s.whatsapp.net",
                "OFF2",
                keys);

            var body = ((List<BinaryNode>)((List<BinaryNode>)node.Content)[0].Content);
            BinaryNode destination = null;
            for (int i = 0; i < body.Count; i++)
            {
                if (body[i].Tag == "destination")
                {
                    destination = body[i];
                    break;
                }
            }

            Assert.NotNull(destination);
            var tos = destination.Content as List<BinaryNode>;
            Assert.NotNull(tos);
            Assert.Equal(2, tos.Count);
            Assert.Equal("to", tos[0].Tag);
            Assert.Equal("1555:0@s.whatsapp.net", tos[0].GetAttribute("jid"));
        }

        [Fact]
        public void BuildTerminate_matches_meowcaller_hangup_wrapper()
        {
            BinaryNode node = CallStanzaBuilders.BuildTerminate(
                "XYZ",
                "1555@s.whatsapp.net",
                "1555@s.whatsapp.net",
                reason: null,
                meJid: null,
                stanzaId: "TERM1");

            Assert.Equal("TERM1", node.GetAttribute("id"));
            Assert.Equal("1555@s.whatsapp.net", node.GetAttribute("to"));
            Assert.Null(node.GetAttribute("from"));

            var children = node.Content as List<BinaryNode>;
            Assert.NotNull(children);
            Assert.Equal("terminate", children[0].Tag);
            Assert.Null(children[0].GetAttribute("reason"));
        }
    }
}
