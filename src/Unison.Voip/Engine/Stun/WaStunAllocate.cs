// =============================================================================
// WaStunAllocate
//
// WhatsApp relay Allocate builders: WASM/Web DataChannel allocate (static
// template, per-call stream SSRCs, group subscriptions + HBH-FEC) and the APK
// allocate, plus their protobuf payloads (attrs 0x4021 / 0x4024 / 0x4025).
// =============================================================================
// Ports: meowcaller stun/stun.go (MIT)
using System;
using System.Collections.Generic;
using System.Text;

namespace Unison.Voip.Engine.Stun
{
    public static class WaStunAllocate
    {
        public const int StreamSsrcCount = 9;
        public const int HbhFecSsrcCount = 2;

        // WASM/Web StreamDescriptors blob (attr 0x4024) captured from a live call.
        private static readonly byte[] WasmStreamDescriptorsTemplate =
        {
            0x0a, 0x06, 0x18, 0xca, 0xbc, 0x85, 0xae, 0x04, 0x0a, 0x08, 0x10, 0x01, 0x18, 0xa5, 0xac, 0xaf,
            0xae, 0x0a, 0x0a, 0x08, 0x10, 0x02, 0x18, 0xd6, 0xa4, 0xe6, 0xf9, 0x0f, 0x0a, 0x08, 0x08, 0x01,
            0x18, 0xf7, 0xdd, 0x9e, 0xb6, 0x0a, 0x0a, 0x0a, 0x08, 0x01, 0x10, 0x01, 0x18, 0xab, 0xcc, 0xb1,
            0xf3, 0x0d, 0x0a, 0x0a, 0x08, 0x01, 0x10, 0x02, 0x18, 0xda, 0xda, 0xef, 0x8a, 0x05, 0x0a, 0x08,
            0x08, 0x02, 0x18, 0xc5, 0xe9, 0xec, 0x8e, 0x0b, 0x0a, 0x0a, 0x08, 0x02, 0x10, 0x01, 0x18, 0xfd,
            0xc2, 0xb1, 0xb6, 0x0f, 0x0a, 0x0a, 0x08, 0x02, 0x10, 0x02, 0x18, 0xb0, 0x97, 0xf7, 0xb2, 0x09,
        };

        // (participant, layer) for each of the nine stream SSRC slots.
        private static readonly uint[,] WasmStreamDescriptorPlan =
        {
            { 0, 0 }, { 0, 1 }, { 0, 2 },
            { 1, 0 }, { 1, 1 }, { 1, 2 },
            { 2, 0 }, { 2, 1 }, { 2, 2 },
        };

        private static readonly byte[] AudioLayerId = Encoding.ASCII.GetBytes("audio");
        private static readonly byte[] OpusPayloadType = Encoding.ASCII.GetBytes("OPUS");

        /// <summary>WASM/Web DataChannel Allocate: 0x4000 token + 0x4024 stream desc + 0x0016 endpoint + MI, no FP.</summary>
        public static byte[] BuildWasmStunAllocateRequest(
            byte[] transactionId,
            byte[] relayToken,
            byte[] endpointXor,
            byte[] integrityKey)
        {
            return BuildWasmStunAllocateRequestCore(
                transactionId, relayToken, endpointXor, WasmStreamDescriptorsTemplate, integrityKey);
        }

        /// <summary>
        /// WASM/Web allocate with per-call stream descriptors. The static template only
        /// proves the wire shape; live calls must carry the SSRCs derived from the
        /// current call ID and participant LID.
        /// </summary>
        public static byte[] BuildWasmStunAllocateRequestWithStreamSsrcs(
            byte[] transactionId,
            byte[] relayToken,
            byte[] endpointXor,
            uint[] streamSsrcs,
            byte[] integrityKey)
        {
            return BuildWasmStunAllocateRequestCore(
                transactionId, relayToken, endpointXor, CreateWasmStreamDescriptors(streamSsrcs), integrityKey);
        }

        /// <summary>Adds the participant-specific sender and receiver subscriptions required for group-video forwarding.</summary>
        public static byte[] BuildWasmStunAllocateRequestWithGroupSubscriptions(
            byte[] transactionId,
            byte[] relayToken,
            byte[] endpointXor,
            uint[] streamSsrcs,
            uint appDataSsrc,
            uint[] participantPids,
            byte[] integrityKey)
        {
            return BuildWasmStunAllocateRequestWithGroupSubscriptionsAndHBHFEC(
                transactionId,
                relayToken,
                endpointXor,
                streamSsrcs,
                appDataSsrc,
                new uint[HbhFecSsrcCount],
                participantPids,
                integrityKey);
        }

        /// <summary>
        /// Adds the participant subscriptions and hop-by-hop FEC stream descriptors used
        /// by multi-participant group calls. With no participants this is the plain
        /// per-call allocate; HBH-FEC descriptors are only sent for more than one.
        /// </summary>
        public static byte[] BuildWasmStunAllocateRequestWithGroupSubscriptionsAndHBHFEC(
            byte[] transactionId,
            byte[] relayToken,
            byte[] endpointXor,
            uint[] streamSsrcs,
            uint appDataSsrc,
            uint[] hbhFecSsrcs,
            uint[] participantPids,
            byte[] integrityKey)
        {
            uint[] pids = NormalizedParticipantPids(participantPids);
            if (pids.Length == 0)
            {
                return BuildWasmStunAllocateRequestWithStreamSsrcs(
                    transactionId, relayToken, endpointXor, streamSsrcs, integrityKey);
            }

            RequireEndpoint(endpointXor);
            byte[] streamDescriptors = pids.Length > 1
                ? CreateWasmStreamDescriptorsWithHBHFEC(streamSsrcs, hbhFecSsrcs)
                : CreateWasmStreamDescriptors(streamSsrcs);

            var participantCount = new List<byte>();
            StunProtobuf.AppendUvarint(participantCount, (ulong)pids.Length);

            var attrs = new List<byte>();
            attrs.AddRange(StunCodec.StunAttr(StunCodec.AttrRelayToken, relayToken));
            attrs.AddRange(StunCodec.StunAttr(
                StunCodec.AttrSenderSubscriptionsV2,
                CreateWasmGroupSenderSubscriptions(streamSsrcs, appDataSsrc, pids)));
            attrs.AddRange(StunCodec.StunAttr(
                StunCodec.AttrReceiverSubscriptions,
                CreateWasmGroupReceiverSubscriptions(pids)));
            attrs.AddRange(StunCodec.StunAttr(StunCodec.AttrStreamDescriptors, streamDescriptors));
            attrs.AddRange(StunCodec.StunAttr(StunCodec.AttrParticipantCount, participantCount.ToArray()));
            attrs.AddRange(StunCodec.StunAttr(StunCodec.AttrWasmRelayEndpoint, CreateWasmRelayEndpointAttr(endpointXor)));
            return StunCodec.EncodeStunRequest(
                StunCodec.MsgAllocateRequest, transactionId, attrs.ToArray(), integrityKey, false);
        }

        /// <summary>WASM/Web attr 0x4024 payload for the nine relay stream SSRCs in slot order; zero SSRCs are skipped.</summary>
        public static byte[] CreateWasmStreamDescriptors(uint[] ssrcs)
        {
            return CreateWasmStreamDescriptorsWithHBHFEC(ssrcs, new uint[HbhFecSsrcCount]);
        }

        /// <summary>voip.SenderSubscriptions (WASM, attr 0x4000).</summary>
        public static byte[] CreateVoipSenderSubscriptions(uint ssrc)
        {
            var sender = new List<byte>();
            StunProtobuf.PbVarintField(sender, 3, ssrc); // ssrc
            StunProtobuf.PbVarintField(sender, 5, 0);    // stream_layer = AUDIO(0)
            StunProtobuf.PbVarintField(sender, 6, 0);    // payload_type = MEDIA(0)
            return StunProtobuf.LenDelim(1, sender);
        }

        /// <summary>wa.voip.SenderSubscriptions (APK, attr 0x4025).</summary>
        public static byte[] CreateApkSenderSubscriptions(uint ssrc, uint? pid)
        {
            var ssrcLayers = new List<byte>();
            StunProtobuf.PbVarintField(ssrcLayers, 1, StunProtobuf.PbZigzag(ssrc)); // ssrcs[0] (sint64)
            if (pid.HasValue)
            {
                var participant = new List<byte>();
                StunProtobuf.PbVarintField(participant, 1, StunProtobuf.PbZigzag(pid.Value)); // pid (sint64)
                StunProtobuf.PbLenDelim(participant, 2, AudioLayerId);                        // layerId
                StunProtobuf.PbLenDelim(ssrcLayers, 2, participant);
            }
            var ext = new List<byte>(StunProtobuf.LenDelim(1, ssrcLayers)); // ssrcLayers
            return StunProtobuf.LenDelim(1, ext);                          // subscriptions[0]
        }

        /// <summary>wa.voip.StreamDescriptors (APK, attr 0x4024).</summary>
        public static byte[] CreateApkStreamDescriptors(uint ssrc)
        {
            var sd = new List<byte>();
            StunProtobuf.PbLenDelim(sd, 1, AudioLayerId);                     // stream_layer
            StunProtobuf.PbLenDelim(sd, 2, OpusPayloadType);                  // payload_type
            StunProtobuf.PbVarintField(sd, 3, StunProtobuf.PbZigzag(ssrc));   // ssrc (sint64)
            StunProtobuf.PbVarintField(sd, 4, 0);                             // is_uplink_prefetch_enabled = false
            return StunProtobuf.LenDelim(1, sd);
        }

        /// <summary>APK Allocate: 0x4000 token + 0x4025 sender subs + 0x4024 stream desc + MI.</summary>
        public static byte[] BuildAndroidStunAllocateRequest(
            byte[] transactionId,
            byte[] relayToken,
            uint ssrc,
            uint? pid,
            byte[] integrityKey,
            bool includeFingerprint)
        {
            var attrs = new List<byte>();
            attrs.AddRange(StunCodec.StunAttr(StunCodec.AttrRelayToken, relayToken));
            attrs.AddRange(StunCodec.StunAttr(StunCodec.AttrSenderSubscriptionsV2, CreateApkSenderSubscriptions(ssrc, pid)));
            attrs.AddRange(StunCodec.StunAttr(StunCodec.AttrStreamDescriptors, CreateApkStreamDescriptors(ssrc)));
            return StunCodec.EncodeStunRequest(
                StunCodec.MsgAllocateRequest, transactionId, attrs.ToArray(), integrityKey, includeFingerprint);
        }

        private static byte[] BuildWasmStunAllocateRequestCore(
            byte[] transactionId,
            byte[] relayToken,
            byte[] endpointXor,
            byte[] streamDescriptors,
            byte[] integrityKey)
        {
            RequireEndpoint(endpointXor);
            var attrs = new List<byte>();
            attrs.AddRange(StunCodec.StunAttr(StunCodec.AttrRelayToken, relayToken));
            attrs.AddRange(StunCodec.StunAttr(StunCodec.AttrStreamDescriptors, streamDescriptors));
            attrs.AddRange(StunCodec.StunAttr(StunCodec.AttrWasmRelayEndpoint, CreateWasmRelayEndpointAttr(endpointXor)));
            return StunCodec.EncodeStunRequest(
                StunCodec.MsgAllocateRequest, transactionId, attrs.ToArray(), integrityKey, false);
        }

        // WASM attr 0x0016 value: 00 01 + 6-byte XOR endpoint.
        private static byte[] CreateWasmRelayEndpointAttr(byte[] endpointXor)
        {
            var buf = new byte[2 + StunCodec.XorRelayEndpointLength];
            buf[1] = 1;
            Buffer.BlockCopy(endpointXor, 0, buf, 2, StunCodec.XorRelayEndpointLength);
            return buf;
        }

        private static uint[] NormalizedParticipantPids(uint[] participantPids)
        {
            if (participantPids == null || participantPids.Length == 0)
            {
                return new uint[0];
            }
            var sorted = (uint[])participantPids.Clone();
            Array.Sort(sorted);
            var unique = new List<uint>(sorted.Length);
            foreach (uint pid in sorted)
            {
                if (unique.Count == 0 || unique[unique.Count - 1] != pid)
                {
                    unique.Add(pid);
                }
            }
            return unique.ToArray();
        }

        private static byte[] CreateWasmGroupSenderSubscriptions(uint[] streamSsrcs, uint appDataSsrc, uint[] participantPids)
        {
            StunCodec.RequireLength(streamSsrcs, StreamSsrcCount, nameof(streamSsrcs));
            var output = new List<byte>();
            output.AddRange(CreateWasmSenderSubscription(Slice(streamSsrcs, 3, 3), participantPids, true));
            output.AddRange(CreateWasmSenderSubscription(Slice(streamSsrcs, 6, 3), null, false));
            output.AddRange(CreateWasmSenderSubscription(Slice(streamSsrcs, 0, 3), participantPids, false));
            output.AddRange(CreateWasmSenderSubscription(new[] { appDataSsrc }, participantPids, false));
            return output.ToArray();
        }

        private static byte[] CreateWasmSenderSubscription(uint[] ssrcs, uint[] participantPids, bool video)
        {
            var packedSsrcs = new List<byte>();
            foreach (uint ssrc in ssrcs)
            {
                if (ssrc != 0)
                {
                    StunProtobuf.AppendUvarint(packedSsrcs, ssrc);
                }
            }

            var subscription = new List<byte>();
            StunProtobuf.PbLenDelim(subscription, 1, packedSsrcs);
            if (participantPids != null)
            {
                foreach (uint pid in participantPids)
                {
                    var participant = new List<byte>();
                    StunProtobuf.PbVarintField(participant, 1, pid);
                    if (video)
                    {
                        StunProtobuf.PbVarintField(participant, 2, 1);
                    }
                    StunProtobuf.PbLenDelim(subscription, 2, participant);
                }
            }

            var wrapper = new List<byte>(StunProtobuf.LenDelim(1, subscription));
            return StunProtobuf.LenDelim(1, wrapper);
        }

        private static byte[] CreateWasmGroupReceiverSubscriptions(uint[] participantPids)
        {
            var output = new List<byte>();
            foreach (uint pid in participantPids)
            {
                var participant = new List<byte>();
                StunProtobuf.PbVarintField(participant, 1, pid);
                StunProtobuf.PbLenDelim(output, 2, participant);
            }
            return output.ToArray();
        }

        private static byte[] CreateWasmStreamDescriptorsWithHBHFEC(uint[] ssrcs, uint[] hbhFecSsrcs)
        {
            StunCodec.RequireLength(ssrcs, StreamSsrcCount, nameof(ssrcs));
            StunCodec.RequireLength(hbhFecSsrcs, HbhFecSsrcCount, nameof(hbhFecSsrcs));
            var output = new List<byte>();
            for (int i = 0; i < StreamSsrcCount; i++)
            {
                uint ssrc = ssrcs[i];
                if (ssrc == 0)
                {
                    continue;
                }
                uint participantSlot = WasmStreamDescriptorPlan[i, 0];
                uint layer = WasmStreamDescriptorPlan[i, 1];
                var inner = new List<byte>();
                if (participantSlot != 0)
                {
                    StunProtobuf.PbVarintField(inner, 1, participantSlot);
                }
                if (layer != 0)
                {
                    StunProtobuf.PbVarintField(inner, 2, layer);
                }
                StunProtobuf.PbVarintField(inner, 3, ssrc);
                StunProtobuf.PbLenDelim(output, 1, inner);
            }
            for (int i = 0; i < HbhFecSsrcCount; i++)
            {
                uint ssrc = hbhFecSsrcs[i];
                if (ssrc == 0)
                {
                    continue;
                }
                var inner = new List<byte>();
                StunProtobuf.PbVarintField(inner, 1, (ulong)(i + 3));
                StunProtobuf.PbVarintField(inner, 2, 3);
                StunProtobuf.PbVarintField(inner, 3, ssrc);
                StunProtobuf.PbLenDelim(output, 1, inner);
            }
            return output.ToArray();
        }

        private static uint[] Slice(uint[] source, int offset, int count)
        {
            var result = new uint[count];
            Array.Copy(source, offset, result, 0, count);
            return result;
        }

        private static void RequireEndpoint(byte[] endpointXor)
        {
            StunCodec.RequireLength(endpointXor, StunCodec.XorRelayEndpointLength, nameof(endpointXor));
        }
    }
}
