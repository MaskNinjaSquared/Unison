// =============================================================================
// CallRelayParser
//
// Pure readers for the relay material carried on <call> action children.
//
// Ports: meowcaller engine.go findRelay / parseRelayData / parseIndexedTokens /
// decodeLatency / getMediaRelayEndpoint and onRelayLatency's probe walk
// =============================================================================
using System.Collections.Generic;
using System.Globalization;
using Unison.Baileys.Protocol;
using Unison.Socket.Models;

namespace Unison.Socket.Notifications
{
    public static class CallRelayParser
    {
        private const int MaxRelayTokens = 64;

        /// <summary>Relay latency wire encoding is 0x2000000 + rttMs.</summary>
        private const uint LatencyBias = 0x02000000;

        /// <summary>Finds &lt;relay&gt; anywhere under <paramref name="node"/>; null when absent.</summary>
        public static CallRelayInfo FindRelay(BinaryNode node)
        {
            BinaryNode relay = FindChild(node, "relay");
            return relay == null ? null : ParseRelay(relay);
        }

        public static List<CallRelayLatencyProbe> ReadLatencyProbes(BinaryNode action)
        {
            var probes = new List<CallRelayLatencyProbe>();
            if (action == null || action.Tag != "relaylatency")
            {
                return probes;
            }

            foreach (BinaryNode te in Children(action))
            {
                if (te.Tag != "te")
                {
                    continue;
                }

                probes.Add(new CallRelayLatencyProbe
                {
                    LatencyMs = DecodeLatency(te.GetAttribute("latency")),
                    RelayName = te.GetAttribute("relay_name") ?? string.Empty,
                    AddressBytes = te.GetContentBytes()
                });
            }

            return probes;
        }

        /// <summary>
        /// Inbound: the caller's FNA relay, or the relay never bridges their uplink. Otherwise an
        /// outbound (non-FNA, auth_token_id≠0) endpoint, then any non-FNA, then the first.
        /// </summary>
        public static CallRelayEndpoint SelectMediaEndpoint(CallRelayInfo relay, bool inbound)
        {
            if (relay == null || relay.Endpoints.Count == 0)
            {
                return null;
            }

            if (inbound)
            {
                foreach (CallRelayEndpoint e in relay.Endpoints)
                {
                    if (e.IsFna)
                    {
                        return e;
                    }
                }
            }

            foreach (CallRelayEndpoint e in relay.Endpoints)
            {
                if (!e.IsFna && e.AuthTokenId != 0)
                {
                    return e;
                }
            }

            foreach (CallRelayEndpoint e in relay.Endpoints)
            {
                if (!e.IsFna)
                {
                    return e;
                }
            }

            return relay.Endpoints[0];
        }

        private static CallRelayInfo ParseRelay(BinaryNode relay)
        {
            var info = new CallRelayInfo();
            string peerPid = relay.GetAttribute("peer_pid");

            foreach (BinaryNode child in Children(relay))
            {
                switch (child.Tag)
                {
                    case "key":
                        info.Key = child.GetContentBytes();
                        break;

                    case "token":
                        AddIndexedToken(info.Tokens, child);
                        break;

                    case "participant":
                        if (!string.IsNullOrEmpty(peerPid) && child.GetAttribute("pid") == peerPid)
                        {
                            info.PeerJid = child.GetAttribute("jid");
                        }

                        break;

                    case "te2":
                        CallRelayEndpoint endpoint = ParseTe2(child);
                        if (endpoint != null)
                        {
                            info.Endpoints.Add(endpoint);
                        }

                        break;
                }
            }

            return info;
        }

        private static void AddIndexedToken(List<byte[]> tokens, BinaryNode token)
        {
            byte[] value = token.GetContentBytes();
            if (value == null)
            {
                return;
            }

            int id = tokens.Count;
            int parsed;
            string raw = token.GetAttribute("id");
            if (!string.IsNullOrEmpty(raw) &&
                int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                id = parsed;
            }

            if (id < 0 || id >= MaxRelayTokens)
            {
                return;
            }

            while (tokens.Count <= id)
            {
                tokens.Add(null);
            }

            tokens[id] = value;
        }

        private static CallRelayEndpoint ParseTe2(BinaryNode te2)
        {
            byte[] address = te2.GetContentBytes();
            if (address == null || address.Length != 6)
            {
                return null;
            }

            return new CallRelayEndpoint
            {
                RelayId = AttrUInt(te2, "relay_id"),
                RelayName = te2.GetAttribute("relay_name") ?? string.Empty,
                TokenId = AttrUInt(te2, "token_id"),
                AuthTokenId = AttrUInt(te2, "auth_token_id"),
                IsFna = te2.GetAttribute("is_fna") == "1",
                Ipv4 = address[0] + "." + address[1] + "." + address[2] + "." + address[3],
                Port = (address[4] << 8) | address[5]
            };
        }

        private static uint DecodeLatency(string encoded)
        {
            uint value;
            if (!uint.TryParse(encoded, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ||
                value < LatencyBias)
            {
                return 0;
            }

            return value - LatencyBias;
        }

        private static uint AttrUInt(BinaryNode node, string key)
        {
            uint value;
            return uint.TryParse(
                node.GetAttribute(key),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value)
                ? value
                : 0;
        }

        private static BinaryNode FindChild(BinaryNode node, string tag)
        {
            if (node == null)
            {
                return null;
            }

            if (node.Tag == tag)
            {
                return node;
            }

            foreach (BinaryNode child in Children(node))
            {
                BinaryNode found = FindChild(child, tag);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static IEnumerable<BinaryNode> Children(BinaryNode node) =>
            node.GetAllChildren() ?? new List<BinaryNode>();
    }
}
