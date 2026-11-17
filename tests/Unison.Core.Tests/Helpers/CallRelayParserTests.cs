using System.Collections.Generic;
using System.Text;
using Unison.Baileys.Protocol;
using Unison.Socket.Models;
using Unison.Socket.Notifications;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class CallRelayParserTests
    {
        private static BinaryNode Node(string tag, Dictionary<string, string> attrs = null, object content = null)
        {
            attrs = attrs ?? new Dictionary<string, string>();
            if (content is List<BinaryNode> children)
            {
                return new BinaryNode(tag, attrs, children);
            }

            if (content is byte[] bytes)
            {
                return new BinaryNode(tag, attrs, bytes);
            }

            return new BinaryNode(tag, attrs);
        }

        private static Dictionary<string, string> A(params string[] kv)
        {
            var d = new Dictionary<string, string>();
            for (int i = 0; i + 1 < kv.Length; i += 2)
            {
                d[kv[i]] = kv[i + 1];
            }

            return d;
        }

        private static BinaryNode Te2(string relayName, string tokenId, string authTokenId, bool fna, byte[] addr) =>
            Node(
                "te2",
                A(
                    "relay_id", "7",
                    "relay_name", relayName,
                    "token_id", tokenId,
                    "auth_token_id", authTokenId,
                    "is_fna", fna ? "1" : "0"),
                addr);

        [Fact]
        public void Parses_key_indexed_tokens_ipv4_endpoints_and_peer()
        {
            var relay = Node(
                "relay",
                A("peer_pid", "2"),
                new List<BinaryNode>
                {
                    Node("key", null, Encoding.ASCII.GetBytes("relaykey")),
                    Node("token", A("id", "1"), new byte[] { 0xB1 }),
                    Node("token", A("id", "0"), new byte[] { 0xA0 }),
                    Node("participant", A("pid", "1", "jid", "self@lid")),
                    Node("participant", A("pid", "2", "jid", "peer:3@lid")),
                    Te2("gru1", "1", "0", true, new byte[] { 157, 240, 1, 2, 0x0D, 0x96 }),
                    Te2("gru2", "0", "5", false, new byte[] { 10, 0, 0, 1, 0x0D, 0x97 }),
                    Te2("v6", "0", "0", false, new byte[18])
                });
            var offer = Node("offer", null, new List<BinaryNode> { Node("audio"), relay });

            CallRelayInfo info = CallRelayParser.FindRelay(offer);

            Assert.NotNull(info);
            Assert.Equal(Encoding.ASCII.GetBytes("relaykey"), info.Key);
            Assert.Equal(2, info.Tokens.Count);
            Assert.Equal(new byte[] { 0xA0 }, info.Tokens[0]);
            Assert.Equal(new byte[] { 0xB1 }, info.Tokens[1]);
            Assert.Equal("peer:3@lid", info.PeerJid);
            Assert.Equal(2, info.Endpoints.Count);

            CallRelayEndpoint fna = info.Endpoints[0];
            Assert.Equal("gru1", fna.RelayName);
            Assert.Equal(7u, fna.RelayId);
            Assert.Equal(1u, fna.TokenId);
            Assert.True(fna.IsFna);
            Assert.Equal("157.240.1.2", fna.Ipv4);
            Assert.Equal(3478, fna.Port);

            Assert.Equal(5u, info.Endpoints[1].AuthTokenId);
            Assert.False(info.Endpoints[1].IsFna);
        }

        [Fact]
        public void No_relay_child_returns_null()
        {
            Assert.Null(CallRelayParser.FindRelay(Node("offer", null, new List<BinaryNode> { Node("audio") })));
        }

        [Fact]
        public void Token_ids_at_or_above_cap_are_dropped()
        {
            var relay = Node(
                "relay",
                null,
                new List<BinaryNode> { Node("token", A("id", "64"), new byte[] { 1 }) });

            Assert.Empty(CallRelayParser.FindRelay(relay).Tokens);
        }

        [Fact]
        public void Media_endpoint_prefers_fna_for_inbound_then_outbound_auth()
        {
            var info = new CallRelayInfo();
            info.Endpoints.Add(new CallRelayEndpoint { RelayName = "plain", IsFna = false, AuthTokenId = 0 });
            info.Endpoints.Add(new CallRelayEndpoint { RelayName = "auth", IsFna = false, AuthTokenId = 3 });
            info.Endpoints.Add(new CallRelayEndpoint { RelayName = "fna", IsFna = true });

            Assert.Equal("fna", CallRelayParser.SelectMediaEndpoint(info, inbound: true).RelayName);
            Assert.Equal("auth", CallRelayParser.SelectMediaEndpoint(info, inbound: false).RelayName);
        }

        [Fact]
        public void Reads_relaylatency_probes_and_decodes_latency()
        {
            var action = Node(
                "relaylatency",
                A("call-id", "C1", "call-creator", "peer@lid"),
                new List<BinaryNode>
                {
                    Node("te", A("latency", (0x02000000 + 42).ToString(), "relay_name", "gru1"), new byte[] { 1, 2, 3, 4, 5, 6 }),
                    Node("te", A("latency", "12", "relay_name", "bad"), new byte[] { 9 }),
                    Node("destination")
                });

            List<CallRelayLatencyProbe> probes = CallRelayParser.ReadLatencyProbes(action);

            Assert.Equal(2, probes.Count);
            Assert.Equal(42u, probes[0].LatencyMs);
            Assert.Equal("gru1", probes[0].RelayName);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, probes[0].AddressBytes);
            Assert.Equal(0u, probes[1].LatencyMs);
        }

        [Fact]
        public void Non_relaylatency_action_has_no_probes()
        {
            Assert.Empty(CallRelayParser.ReadLatencyProbes(Node("offer")));
        }
    }
}
