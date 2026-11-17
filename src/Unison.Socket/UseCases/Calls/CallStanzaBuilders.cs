// =============================================================================
// CallStanzaBuilders
//
// Outbound <call> wrappers for preaccept / accept / reject / terminate.
// Ports: meowcaller signaling/stanza.go + Baileys rejectCall (MD companion attrs).
// Child order is load-bearing (server 439 when wrong).
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Baileys.Protocol;

namespace Unison.Socket.UseCases.Calls
{
    public static class CallStanzaBuilders
    {
        /// <summary>meowcaller <c>CapabilityOffer</c> (offer / video preaccept).</summary>
        public static readonly byte[] CapabilityOfferBlob =
            { 0x01, 0x05, 0xf7, 0x09, 0xe0, 0xbb, 0x13 };

        /// <summary>meowcaller <c>CapabilityPreaccept</c> (audio preaccept).</summary>
        public static readonly byte[] CapabilityPreacceptBlob =
            { 0x01, 0x05, 0xf7, 0x09, 0xe0, 0xbb, 0x07 };

        /// <summary>
        /// Eager prep on inbound offer (meowcaller <c>sendPreaccept</c>):
        /// audio(16000) → encopt → capability(Preaccept blob).
        /// </summary>
        public static BinaryNode BuildPreaccept(
            string callId,
            string toJid,
            string callCreatorJid,
            string stanzaId,
            string meJid = null)
        {
            Require(callId, nameof(callId));
            Require(toJid, nameof(toJid));
            Require(callCreatorJid, nameof(callCreatorJid));
            Require(stanzaId, nameof(stanzaId));

            var children = new List<BinaryNode>
            {
                AudioOpus("16000"),
                new BinaryNode(
                    "encopt",
                    new Dictionary<string, string> { { "keygen", "2" } }),
                new BinaryNode(
                    "capability",
                    new Dictionary<string, string> { { "ver", "1" } },
                    CapabilityPreacceptBlob)
            };

            var action = new BinaryNode(
                "preaccept",
                ActionAttrs(callId, callCreatorJid),
                children);
            return WrapCall(toJid, action, meJid, stanzaId);
        }

        /// <summary>
        /// Minimal inbound <c>accept</c> (meowcaller <c>BuildAccept</c> without relay yet):
        /// audio → [video] → net medium=2 → encopt → metadata.
        /// Stops the caller ring; media still needs the engine.
        /// </summary>
        public static BinaryNode BuildAccept(
            string callId,
            string toJid,
            string callCreatorJid,
            string stanzaId,
            bool isVideo = false,
            string meJid = null)
        {
            Require(callId, nameof(callId));
            Require(toJid, nameof(toJid));
            Require(callCreatorJid, nameof(callCreatorJid));
            Require(stanzaId, nameof(stanzaId));

            var children = new List<BinaryNode> { AudioOpus("16000") };
            if (isVideo)
            {
                children.Add(new BinaryNode(
                    "video",
                    new Dictionary<string, string>
                    {
                        { "dec", "H264" },
                        { "device_orientation", "0" }
                    }));
            }

            children.Add(new BinaryNode(
                "net",
                new Dictionary<string, string> { { "medium", "2" } }));
            children.Add(new BinaryNode(
                "encopt",
                new Dictionary<string, string> { { "keygen", "2" } }));
            children.Add(new BinaryNode(
                "metadata",
                new Dictionary<string, string>
                {
                    { "peer_abtest_bucket_id_list", "125208,94276" }
                }));

            var action = new BinaryNode(
                "accept",
                ActionAttrs(callId, callCreatorJid),
                children);
            return WrapCall(toJid, action, meJid, stanzaId);
        }

        /// <summary>
        /// Baileys <c>rejectCall</c> + meowcaller <c>BuildReject</c>:
        /// <c>&lt;call id from=me to=peer&gt;&lt;reject call-id call-creator count="0"/&gt;&lt;/call&gt;</c>
        /// </summary>
        public static BinaryNode BuildReject(
            string callId,
            string toJid,
            string callCreatorJid,
            string meJid = null,
            string stanzaId = null)
        {
            Require(callId, nameof(callId));
            Require(toJid, nameof(toJid));
            Require(callCreatorJid, nameof(callCreatorJid));

            var action = new BinaryNode(
                "reject",
                new Dictionary<string, string>
                {
                    { "call-id", callId },
                    { "call-creator", callCreatorJid },
                    // Baileys rejectCall always sends count="0".
                    { "count", "0" }
                });

            return WrapCall(toJid, action, meJid, stanzaId);
        }

        /// <summary>
        /// <c>&lt;call id to=…&gt;&lt;terminate call-id call-creator [reason]/gt;&lt;/call&gt;</c>
        /// </summary>
        public static BinaryNode BuildTerminate(
            string callId,
            string toJid,
            string callCreatorJid,
            string reason = null,
            string meJid = null,
            string stanzaId = null)
        {
            Require(callId, nameof(callId));
            Require(toJid, nameof(toJid));
            Require(callCreatorJid, nameof(callCreatorJid));

            var attrs = new Dictionary<string, string>
            {
                { "call-id", callId },
                { "call-creator", callCreatorJid }
            };
            if (!string.IsNullOrEmpty(reason))
            {
                attrs["reason"] = reason;
            }

            var action = new BinaryNode("terminate", attrs);
            return WrapCall(toJid, action, meJid, stanzaId);
        }

        /// <summary>One per-device encrypted callKey entry inside an offer (meowcaller OfferDeviceKey).</summary>
        public sealed class OfferDeviceKey
        {
            public string DeviceJid { get; set; }

            public byte[] Ciphertext { get; set; }

            /// <summary>"pkmsg" or "msg".</summary>
            public string EncType { get; set; }
        }

        /// <summary>
        /// meowcaller <c>BuildOffer</c> child order:
        /// [privacy] → audio(8k) → audio(16k) → [video] → net medium=3 → capability →
        /// destination|enc → encopt → [device-identity].
        /// Wrapper <c>from</c> omitted (caller); <c>id</c> required for server ack.
        /// </summary>
        public static BinaryNode BuildOffer(
            string callId,
            string toJid,
            string callCreatorJid,
            string stanzaId,
            IReadOnlyList<OfferDeviceKey> deviceKeys,
            byte[] privacyToken = null,
            byte[] capability = null,
            byte[] deviceIdentity = null,
            bool isVideo = false)
        {
            Require(callId, nameof(callId));
            Require(toJid, nameof(toJid));
            Require(callCreatorJid, nameof(callCreatorJid));
            Require(stanzaId, nameof(stanzaId));
            if (deviceKeys == null || deviceKeys.Count == 0)
            {
                throw new ArgumentException("At least one device key is required", nameof(deviceKeys));
            }

            var children = new List<BinaryNode>();
            if (privacyToken != null && privacyToken.Length > 0)
            {
                children.Add(new BinaryNode("privacy", null, privacyToken));
            }

            children.Add(AudioOpus("8000"));
            children.Add(AudioOpus("16000"));
            if (isVideo)
            {
                children.Add(new BinaryNode(
                    "video",
                    new Dictionary<string, string>
                    {
                        { "orientation", "0" },
                        { "screen_width", "1920" },
                        { "screen_height", "1080" },
                        { "device_orientation", "0" },
                        { "dec", "H264" },
                        { "enc", "H264" }
                    }));
            }

            children.Add(new BinaryNode(
                "net",
                new Dictionary<string, string> { { "medium", "3" } }));

            byte[] cap = capability ?? CapabilityOfferBlob;
            if (isVideo &&
                cap != null &&
                cap.Length == CapabilityOfferBlob.Length)
            {
                bool same = true;
                for (int i = 0; i < cap.Length; i++)
                {
                    if (cap[i] != CapabilityOfferBlob[i])
                    {
                        same = false;
                        break;
                    }
                }

                if (same)
                {
                    cap = new byte[] { 0x01, 0x05, 0xf7, 0x09, 0xe0, 0xfa, 0x13 };
                }
            }

            if (cap != null && cap.Length > 0)
            {
                children.Add(new BinaryNode(
                    "capability",
                    new Dictionary<string, string> { { "ver", "1" } },
                    cap));
            }

            if (deviceKeys.Count > 1)
            {
                var tos = new List<BinaryNode>(deviceKeys.Count);
                for (int i = 0; i < deviceKeys.Count; i++)
                {
                    OfferDeviceKey dk = deviceKeys[i];
                    tos.Add(new BinaryNode(
                        "to",
                        new Dictionary<string, string> { { "jid", dk.DeviceJid } },
                        new List<BinaryNode> { EncNode(dk) }));
                }

                children.Add(new BinaryNode("destination", null, tos));
            }
            else
            {
                children.Add(EncNode(deviceKeys[0]));
            }

            children.Add(new BinaryNode(
                "encopt",
                new Dictionary<string, string> { { "keygen", "2" } }));

            if (deviceIdentity != null && deviceIdentity.Length > 0)
            {
                children.Add(new BinaryNode("device-identity", null, deviceIdentity));
            }

            var action = new BinaryNode(
                "offer",
                ActionAttrs(callId, callCreatorJid),
                children);
            return WrapCall(toJid, action, meJid: null, stanzaId: stanzaId);
        }

        private static BinaryNode EncNode(OfferDeviceKey dk)
        {
            if (dk == null || string.IsNullOrEmpty(dk.DeviceJid))
            {
                throw new ArgumentException("Offer device key is incomplete");
            }

            return new BinaryNode(
                "enc",
                new Dictionary<string, string>
                {
                    { "v", "2" },
                    { "type", string.IsNullOrEmpty(dk.EncType) ? "msg" : dk.EncType },
                    { "count", "0" }
                },
                dk.Ciphertext ?? new byte[0]);
        }

        /// <summary>
        /// Callee half of the relay election (meowcaller <c>onRelayLatency</c>): echoes one
        /// probe as <c>&lt;relaylatency&gt;&lt;te latency relay_name&gt;addr&lt;/te&gt;</c>.
        /// No destination — that is only for the caller.
        /// </summary>
        public static BinaryNode BuildRelayLatency(
            string callId,
            string toJid,
            string callCreatorJid,
            uint latencyMs,
            string relayName,
            byte[] addressBytes,
            string stanzaId)
        {
            Require(callId, nameof(callId));
            Require(toJid, nameof(toJid));
            Require(callCreatorJid, nameof(callCreatorJid));

            var te = new BinaryNode(
                "te",
                new Dictionary<string, string>
                {
                    { "latency", (0x02000000u + latencyMs).ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    { "relay_name", relayName ?? string.Empty }
                },
                addressBytes ?? new byte[0]);

            var action = new BinaryNode(
                "relaylatency",
                ActionAttrs(callId, callCreatorJid),
                new List<BinaryNode> { te });
            return WrapCall(toJid, action, meJid: null, stanzaId: stanzaId);
        }

        private static BinaryNode WrapCall(
            string toJid,
            BinaryNode action,
            string meJid,
            string stanzaId)
        {
            var attrs = new Dictionary<string, string> { { "to", toJid } };
            if (!string.IsNullOrEmpty(stanzaId))
            {
                attrs["id"] = stanzaId;
            }

            // Baileys rejectCall sets from=me on the outer call wrapper.
            if (!string.IsNullOrEmpty(meJid))
            {
                attrs["from"] = meJid;
            }

            return new BinaryNode(
                "call",
                attrs,
                new List<BinaryNode> { action });
        }

        private static Dictionary<string, string> ActionAttrs(string callId, string callCreatorJid) =>
            new Dictionary<string, string>
            {
                { "call-id", callId },
                { "call-creator", callCreatorJid }
            };

        private static BinaryNode AudioOpus(string rate) =>
            new BinaryNode(
                "audio",
                new Dictionary<string, string>
                {
                    { "enc", "opus" },
                    { "rate", rate }
                });

        private static void Require(string value, string name)
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new ArgumentException(name + " is required", name);
            }
        }
    }
}
