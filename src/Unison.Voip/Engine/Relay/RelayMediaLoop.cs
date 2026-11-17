// =============================================================================
// RelayMediaLoop
//
// Per-call media over the open relay DataChannel. At t+0: STUN allocate +
// consent ping 0x0801, BEFORE any RTP. Then a 1 Hz keepalive (same allocate
// bytes + fresh ping), a frame-paced send loop (captured frame or silence →
// encoder → E2E-SRTP → DataChannel) and a receive loop (relay binding-request
// → signed binding-success; peer RTP → unprotect → decoder → sink).
// The relay learns our SSRC from our first RTP and only bridges the peer after.
// Ports: meowcaller engine_media.go runMedia, engine_group_relay.go (MIT)
// =============================================================================
using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Unison.Voip.Engine.Media;
using Unison.Voip.Engine.Rtp;
using Unison.Voip.Engine.Stun;

namespace Unison.Voip.Engine.Relay
{
    public static class RelayMediaLoop
    {
        public static readonly TimeSpan DefaultKeepalive = TimeSpan.FromSeconds(1);

        public static readonly TimeSpan DefaultFrameInterval =
            TimeSpan.FromMilliseconds(AudioFrameFormat.FrameSamples * 1000.0 / AudioFrameFormat.SampleRate);

        private const int FirstAuxiliaryVideoSlot = 6;
        private const int SsrcDrawAttempts = 64;

        /// <summary>
        /// Builds the allocate frame for the selected endpoint. Returns null when
        /// the config cannot allocate (missing token/key/endpoint).
        /// </summary>
        public static byte[] BuildAllocate(RelayMediaConfig config, RelayMediaEndpoint endpoint, out uint[] streamSsrcs)
        {
            streamSsrcs = null;
            if (config == null || endpoint == null)
            {
                return null;
            }

            if (config.RelayKey == null || config.RelayKey.Length == 0)
            {
                return null;
            }

            int tokenId = (int)endpoint.TokenId;
            if (config.RelayTokens == null ||
                tokenId < 0 ||
                tokenId >= config.RelayTokens.Count ||
                config.RelayTokens[tokenId] == null)
            {
                return null;
            }

            byte[] endpointXor;
            if (!StunCodec.EncodeXorRelayEndpoint(endpoint.Ipv4, (ushort)endpoint.Port, out endpointXor))
            {
                return null;
            }

            string callId = config.CallId ?? string.Empty;
            string participantId = SsrcDerivation.FormatE2ESrtpParticipantID(config.SelfLid ?? string.Empty);
            uint appDataSsrc = SsrcDerivation.DeriveWasmParticipantSsrc(callId, participantId, SsrcDerivation.AppDataSlotWord);
            streamSsrcs = PrepareWasmRelayStreamSsrcs(
                SsrcDerivation.DeriveWasmRelayStreamSsrcs(callId, participantId),
                appDataSsrc,
                RandomUInt32);
            return WaStunAllocate.BuildWasmStunAllocateRequestWithStreamSsrcs(
                RandomTxId(),
                config.RelayTokens[tokenId],
                endpointXor,
                streamSsrcs,
                config.RelayKey);
        }

        /// <summary>
        /// Slots 6..8 (auxiliary video) get fresh random non-zero SSRCs unique against
        /// slots 0..5 and the app-data SSRC.
        /// </summary>
        public static uint[] PrepareWasmRelayStreamSsrcs(uint[] streamSsrcs, uint appDataSsrc, Func<uint> random)
        {
            if (streamSsrcs == null)
            {
                throw new ArgumentNullException(nameof(streamSsrcs));
            }

            if (random == null)
            {
                throw new ArgumentNullException(nameof(random));
            }

            var prepared = (uint[])streamSsrcs.Clone();
            var used = new HashSet<uint>();
            for (int i = 0; i < FirstAuxiliaryVideoSlot && i < prepared.Length; i++)
            {
                if (prepared[i] != 0)
                {
                    used.Add(prepared[i]);
                }
            }

            if (appDataSsrc != 0)
            {
                used.Add(appDataSsrc);
            }

            for (int i = FirstAuxiliaryVideoSlot; i < prepared.Length; i++)
            {
                bool selected = false;
                for (int attempt = 0; attempt < SsrcDrawAttempts && !selected; attempt++)
                {
                    uint candidate = random();
                    if (candidate != 0 && used.Add(candidate))
                    {
                        prepared[i] = candidate;
                        selected = true;
                    }
                }

                if (!selected)
                {
                    throw new InvalidOperationException("Cannot generate a unique auxiliary video SSRC.");
                }
            }

            return prepared;
        }

        public static byte[] BuildConsentPing() => StunCodec.BuildWhatsappPing(RandomTxId());

        /// <summary>Binding-success for a relay binding-request, signed with the relay key; null otherwise.</summary>
        public static byte[] BuildBindingSuccess(byte[] request, byte[] relayKey)
        {
            ushort type;
            byte[] transactionId;
            if (relayKey == null || relayKey.Length == 0 ||
                !StunParse.StunMessageType(request, out type) ||
                type != StunCodec.MsgBindingRequest ||
                !StunParse.StunTransactionID(request, out transactionId) ||
                transactionId == null ||
                transactionId.Length != StunCodec.TransactionIdLength)
            {
                return null;
            }

            return StunCodec.EncodeStunRequest(StunCodec.MsgBindingSuccess, transactionId, null, relayKey, true);
        }

        public static IPEndPoint ToEndPoint(RelayMediaEndpoint endpoint)
        {
            if (endpoint == null || string.IsNullOrEmpty(endpoint.Ipv4))
            {
                return null;
            }

            IPAddress ip;
            if (!IPAddress.TryParse(endpoint.Ipv4, out ip))
            {
                return null;
            }

            return new IPEndPoint(ip, endpoint.Port);
        }

        public static async Task RunAsync(
            IRelayMediaPipe channel,
            RelayMediaConfig config,
            RelayMediaOptions options,
            CancellationToken cancellationToken)
        {
            if (channel == null)
            {
                throw new ArgumentNullException(nameof(channel));
            }

            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            options = options ?? new RelayMediaOptions();
            RelayMediaEndpoint endpoint = RelayMediaEndpointSelector.Select(config.Endpoints, config.Inbound);
            uint[] streamSsrcs;
            byte[] allocate = BuildAllocate(config, endpoint, out streamSsrcs);
            if (allocate == null)
            {
                throw new InvalidOperationException("Cannot build STUN allocate from the media config.");
            }

            MediaPipeline media = CreatePipeline(config);
            await channel.SendAsync(allocate, cancellationToken).ConfigureAwait(false);
            await channel.SendAsync(BuildConsentPing(), cancellationToken).ConfigureAwait(false);

            using (var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                CancellationToken ct = stop.Token;
                var loops = new List<Task>
                {
                    RecvLoopAsync(channel, config, media, options, ct),
                    KeepaliveLoopAsync(channel, allocate, options.KeepalivePeriod, ct)
                };
                if (media != null && options.Encoder != null)
                {
                    loops.Add(SendLoopAsync(channel, media, options, ct));
                }

                Task first = await Task.WhenAny(loops).ConfigureAwait(false);
                stop.Cancel();
                try
                {
                    await Task.WhenAll(loops).ConfigureAwait(false);
                }
                catch
                {
                    // Siblings fail with cancellation or a torn-down channel; the cause is `first`.
                }

                if (first.IsFaulted && !cancellationToken.IsCancellationRequested)
                {
                    await first.ConfigureAwait(false);
                }
            }
        }

        private static MediaPipeline CreatePipeline(RelayMediaConfig config)
        {
            if (config.CallKey == null || config.CallKey.Length == 0 || string.IsNullOrEmpty(config.PeerLid))
            {
                return null;
            }

            string participantId = SsrcDerivation.FormatE2ESrtpParticipantID(config.SelfLid ?? string.Empty);
            uint ssrc = SsrcDerivation.DeriveWasmParticipantSsrc(config.CallId ?? string.Empty, participantId, 0);
            return new MediaPipeline(config.CallKey, config.SelfLid, config.PeerLid, ssrc, AudioFrameFormat.FrameSamples);
        }

        private static async Task KeepaliveLoopAsync(
            IRelayMediaPipe channel,
            byte[] allocate,
            TimeSpan period,
            CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(period, cancellationToken).ConfigureAwait(false);
                await channel.SendAsync(allocate, cancellationToken).ConfigureAwait(false);
                await channel.SendAsync(BuildConsentPing(), cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>Frame-paced from connect, not gated on capture: silence until real audio exists.</summary>
        private static async Task SendLoopAsync(
            IRelayMediaPipe channel,
            MediaPipeline media,
            RelayMediaOptions options,
            CancellationToken cancellationToken)
        {
            var silence = new float[AudioFrameFormat.FrameSamples];
            DateTime next = DateTime.UtcNow;
            while (!cancellationToken.IsCancellationRequested)
            {
                next += options.FrameInterval;
                TimeSpan wait = next - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                }
                else if (-wait > options.FrameInterval)
                {
                    next = DateTime.UtcNow;
                }

                float[] frame = ResolveCaptureFrame(options) ?? silence;
                byte[] payload = options.Encoder.Encode(frame);
                if (payload == null)
                {
                    continue;
                }

                await channel.SendAsync(media.ProtectAudio(payload), cancellationToken).ConfigureAwait(false);
            }
        }

        private static async Task RecvLoopAsync(
            IRelayMediaPipe channel,
            RelayMediaConfig config,
            MediaPipeline media,
            RelayMediaOptions options,
            CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                byte[] packet = await channel.RecvAsync(cancellationToken).ConfigureAwait(false);
                if (packet == null)
                {
                    return;
                }

                switch (RelayPacketClassifier.ClassifyRelayPacket(packet))
                {
                    case RelayPacketKind.Stun:
                        byte[] response = BuildBindingSuccess(packet, config.RelayKey);
                        if (response != null)
                        {
                            await channel.SendAsync(response, cancellationToken).ConfigureAwait(false);
                        }

                        break;
                    case RelayPacketKind.Rtp:
                        await PlayRtpAsync(packet, media, options, cancellationToken).ConfigureAwait(false);
                        break;
                }
            }
        }

        private static float[] ResolveCaptureFrame(RelayMediaOptions options)
        {
            if (options.NextFrame != null)
            {
                return options.NextFrame();
            }

            return options.Source != null ? options.Source.TryReadFrame() : null;
        }

        private static async Task PlayRtpAsync(
            byte[] packet,
            MediaPipeline media,
            RelayMediaOptions options,
            CancellationToken cancellationToken)
        {
            if (media == null || options.Decoder == null || options.Sink == null)
            {
                return;
            }

            RtpHeader header;
            byte[] payload;
            if (!media.TryUnprotectAudio(packet, out header, out payload) ||
                !RtpPacket.IsWhatsappOpusRtpPayload(header.PayloadType))
            {
                return;
            }

            float[] pcm = options.Decoder.Decode(payload);
            if (pcm != null)
            {
                await options.Sink.WriteAsync(pcm, cancellationToken).ConfigureAwait(false);
            }
        }

        private static byte[] RandomTxId()
        {
            var tx = new byte[StunCodec.TransactionIdLength];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(tx);
            }

            return tx;
        }

        private static uint RandomUInt32()
        {
            var buf = new byte[4];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(buf);
            }

            return BitConverter.ToUInt32(buf, 0);
        }
    }
}
