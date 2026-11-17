using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unison.Voip.Abstractions;
using Unison.Voip.Engine.Media;
using Unison.Voip.Engine.Relay;
using Unison.Voip.Engine.Rtp;
using Unison.Voip.Engine.Stun;
using Xunit;

namespace Unison.Core.Tests.Voip.Relay
{
    public sealed class RelayMediaLoopTests
    {
        private const string SelfLid = "12345:0@lid";
        private const string PeerLid = "67890:0@lid";

        [Fact]
        public void Select_inbound_prefers_fna()
        {
            var endpoints = new List<RelayMediaEndpoint>
            {
                new RelayMediaEndpoint { RelayName = "plain", IsFna = false, AuthTokenId = 0 },
                new RelayMediaEndpoint { RelayName = "fna", IsFna = true, Ipv4 = "1.2.3.4", Port = 3478 }
            };

            Assert.Equal("fna", RelayMediaEndpointSelector.Select(endpoints, inbound: true).RelayName);
        }

        [Fact]
        public void BuildAllocate_produces_stun_allocate_request()
        {
            RelayMediaConfig config = SampleConfig();
            uint[] ssrcs;
            byte[] allocate = RelayMediaLoop.BuildAllocate(config, config.Endpoints[0], out ssrcs);

            Assert.NotNull(allocate);
            Assert.Equal(9, ssrcs.Length);
            ushort type;
            Assert.True(StunParse.StunMessageType(allocate, out type));
            Assert.Equal(StunCodec.MsgAllocateRequest, type);
        }

        [Fact]
        public void PrepareStreamSsrcs_keeps_derived_slots_and_randomizes_auxiliary_video()
        {
            uint[] derived = SsrcDerivation.DeriveWasmRelayStreamSsrcs("call-1", SelfLid);
            uint appData = derived[6];
            var draws = new Queue<uint>(new uint[] { 0, derived[0], appData, 0xAAAA0001, 0xAAAA0001, 0xAAAA0002, 0xAAAA0003 });

            uint[] prepared = RelayMediaLoop.PrepareWasmRelayStreamSsrcs(derived, appData, () => draws.Dequeue());

            Assert.Equal(derived.Take(6), prepared.Take(6));
            Assert.Equal(new uint[] { 0xAAAA0001, 0xAAAA0002, 0xAAAA0003 }, prepared.Skip(6));
        }

        [Fact]
        public async Task Run_sends_allocate_then_ping_then_resends_same_allocate()
        {
            var pipe = new RecordingPipe();
            using (var cts = new CancellationTokenSource())
            {
                Task loop = RelayMediaLoop.RunAsync(
                    pipe,
                    SampleConfig(),
                    new RelayMediaOptions { KeepalivePeriod = TimeSpan.FromMilliseconds(40) },
                    cts.Token);

                await WaitUntil(() => pipe.Count >= 4, TimeSpan.FromSeconds(2));
                cts.Cancel();
                await Drain(loop);
            }

            List<byte[]> sent = pipe.Snapshot();
            Assert.True(IsAllocate(sent[0]));
            Assert.True(IsWhatsappPing(sent[1]));
            Assert.Equal(sent[0], sent[2]);
            Assert.True(IsWhatsappPing(sent[3]));
            Assert.NotEqual(sent[1], sent[3]);
            Assert.DoesNotContain(sent, p => RelayPacketClassifier.ClassifyRelayPacket(p) == RelayPacketKind.Rtp);
        }

        [Fact]
        public async Task Run_answers_relay_binding_request_with_signed_binding_success()
        {
            RelayMediaConfig config = SampleConfig();
            var pipe = new RecordingPipe();
            byte[] tx = Enumerable.Range(1, 12).Select(i => (byte)i).ToArray();
            pipe.Enqueue(StunCodec.EncodeStunRequest(StunCodec.MsgBindingRequest, tx, null, null, true));
            using (var cts = new CancellationTokenSource())
            {
                Task loop = RelayMediaLoop.RunAsync(pipe, config, new RelayMediaOptions(), cts.Token);
                await WaitUntil(() => pipe.Snapshot().Any(IsBindingSuccess), TimeSpan.FromSeconds(2));
                cts.Cancel();
                await Drain(loop);
            }

            byte[] response = pipe.Snapshot().First(IsBindingSuccess);
            byte[] echoedTx;
            Assert.True(StunParse.StunTransactionID(response, out echoedTx));
            Assert.Equal(tx, echoedTx);
            Assert.Equal(
                StunCodec.EncodeStunRequest(StunCodec.MsgBindingSuccess, tx, null, config.RelayKey, true),
                response);
        }

        [Fact]
        public async Task Run_with_encoder_sends_protected_silence_rtp_after_the_first_ping()
        {
            RelayMediaConfig config = SampleConfig();
            var pipe = new RecordingPipe();
            var encoder = new RecordingEncoder();
            using (var cts = new CancellationTokenSource())
            {
                Task loop = RelayMediaLoop.RunAsync(
                    pipe,
                    config,
                    new RelayMediaOptions { Encoder = encoder, FrameInterval = TimeSpan.FromMilliseconds(10) },
                    cts.Token);

                await WaitUntil(() => pipe.Snapshot().Count(IsRtp) >= 2, TimeSpan.FromSeconds(2));
                cts.Cancel();
                await Drain(loop);
            }

            List<byte[]> sent = pipe.Snapshot();
            int firstRtp = sent.FindIndex(IsRtp);
            Assert.True(firstRtp > sent.FindIndex(IsWhatsappPing));

            float[] frame = encoder.Frames.First();
            Assert.Equal(AudioFrameFormat.FrameSamples, frame.Length);
            Assert.All(frame, s => Assert.Equal(0f, s));

            uint selfSsrc = SsrcDerivation.DeriveWasmParticipantSsrc(
                config.CallId, SsrcDerivation.FormatE2ESrtpParticipantID(SelfLid), 0);
            var peerSide = new MediaPipeline(config.CallKey, PeerLid, SelfLid, 0, AudioFrameFormat.FrameSamples);
            RtpHeader header;
            byte[] payload;
            Assert.True(peerSide.TryUnprotectAudio(sent[firstRtp], out header, out payload));
            Assert.Equal(selfSsrc, header.Ssrc);
            Assert.Equal((ushort)1, header.SequenceNumber);
            Assert.Equal(RecordingEncoder.Payload, payload);
        }

        [Fact]
        public async Task Run_with_source_sends_captured_frame_when_ready()
        {
            RelayMediaConfig config = SampleConfig();
            var pipe = new RecordingPipe();
            var source = new QueuedSource();
            var tone = new float[AudioFrameFormat.FrameSamples];
            tone[0] = 0.25f;
            source.Push(tone);
            var encoder = new RecordingEncoder();
            using (var cts = new CancellationTokenSource())
            {
                Task loop = RelayMediaLoop.RunAsync(
                    pipe,
                    config,
                    new RelayMediaOptions
                    {
                        Encoder = encoder,
                        Source = source,
                        FrameInterval = TimeSpan.FromMilliseconds(10)
                    },
                    cts.Token);

                await WaitUntil(() => encoder.Frames.Count >= 1, TimeSpan.FromSeconds(2));
                cts.Cancel();
                await Drain(loop);
            }

            float[] first;
            Assert.True(encoder.Frames.TryDequeue(out first));
            Assert.Equal(0.25f, first[0]);
        }

        [Fact]
        public async Task Run_decodes_peer_rtp_into_the_sink()
        {
            RelayMediaConfig config = SampleConfig();
            var pipe = new RecordingPipe();
            var peerTx = new MediaPipeline(config.CallKey, PeerLid, SelfLid, 0x0BADF00D, AudioFrameFormat.FrameSamples);
            byte[] peerPayload = { 0x48, 0x10, 0x20 };
            pipe.Enqueue(peerTx.ProtectAudio(peerPayload));
            var decoder = new RecordingDecoder();
            var sink = new RecordingSink();
            using (var cts = new CancellationTokenSource())
            {
                Task loop = RelayMediaLoop.RunAsync(
                    pipe,
                    config,
                    new RelayMediaOptions { Decoder = decoder, Sink = sink },
                    cts.Token);
                await WaitUntil(() => sink.Count >= 1, TimeSpan.FromSeconds(2));
                cts.Cancel();
                await Drain(loop);
            }

            Assert.Equal(peerPayload, decoder.Payloads.Single());
            Assert.Same(RecordingDecoder.Pcm, sink.Frames.Single());
        }

        private static RelayMediaConfig SampleConfig() =>
            new RelayMediaConfig
            {
                CallId = "call-1",
                SelfLid = SelfLid,
                PeerLid = PeerLid,
                Inbound = true,
                CallKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(),
                RelayKey = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 },
                RelayTokens = new List<byte[]> { new byte[] { 9, 9, 9, 9 } },
                Endpoints =
                {
                    new RelayMediaEndpoint
                    {
                        RelayName = "gru1",
                        TokenId = 0,
                        AuthTokenId = 1,
                        IsFna = true,
                        Ipv4 = "157.240.226.133",
                        Port = 3478
                    }
                }
            };

        private static bool IsWhatsappPing(byte[] packet) =>
            packet != null && packet.Length >= 4 && packet[0] == 0x08 && packet[1] == 0x01;

        private static bool IsAllocate(byte[] packet) => HasStunType(packet, StunCodec.MsgAllocateRequest);

        private static bool IsBindingSuccess(byte[] packet) => HasStunType(packet, StunCodec.MsgBindingSuccess);

        private static bool IsRtp(byte[] packet) =>
            RelayPacketClassifier.ClassifyRelayPacket(packet) == RelayPacketKind.Rtp;

        private static bool HasStunType(byte[] packet, ushort expected)
        {
            ushort type;
            return StunParse.StunMessageType(packet, out type) && type == expected;
        }

        private static async Task Drain(Task loop)
        {
            try
            {
                await loop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(10);
            }

            Assert.True(condition(), "condition not met before timeout");
        }

        private sealed class RecordingPipe : IRelayMediaPipe
        {
            private readonly ConcurrentQueue<byte[]> _inbox = new ConcurrentQueue<byte[]>();
            private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
            private readonly List<byte[]> _sent = new List<byte[]>();

            public int Count
            {
                get
                {
                    lock (_sent)
                    {
                        return _sent.Count;
                    }
                }
            }

            public List<byte[]> Snapshot()
            {
                lock (_sent)
                {
                    return _sent.ToList();
                }
            }

            public void Enqueue(byte[] packet)
            {
                _inbox.Enqueue(packet);
                _signal.Release();
            }

            public Task SendAsync(byte[] data, CancellationToken cancellationToken)
            {
                lock (_sent)
                {
                    _sent.Add((byte[])data.Clone());
                }

                return Task.CompletedTask;
            }

            public async Task<byte[]> RecvAsync(CancellationToken cancellationToken)
            {
                await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
                byte[] next;
                return _inbox.TryDequeue(out next) ? next : null;
            }

            public void Dispose()
            {
            }
        }

        private sealed class RecordingEncoder : IAudioFrameEncoder
        {
            public static readonly byte[] Payload = { 0x48, 0xAA, 0xBB };

            public ConcurrentQueue<float[]> Frames { get; } = new ConcurrentQueue<float[]>();

            public byte[] Encode(float[] pcm)
            {
                Frames.Enqueue(pcm);
                return Payload;
            }
        }

        private sealed class RecordingDecoder : IAudioFrameDecoder
        {
            public static readonly float[] Pcm = new float[AudioFrameFormat.FrameSamples];

            public ConcurrentQueue<byte[]> Payloads { get; } = new ConcurrentQueue<byte[]>();

            public float[] Decode(byte[] payload)
            {
                Payloads.Enqueue(payload);
                return Pcm;
            }
        }

        private sealed class RecordingSink : ICallAudioSink
        {
            public ConcurrentQueue<float[]> Frames { get; } = new ConcurrentQueue<float[]>();

            public int Count => Frames.Count;

            public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task WriteAsync(float[] pcm, CancellationToken cancellationToken)
            {
                Frames.Enqueue(pcm);
                return Task.CompletedTask;
            }

            public void Dispose()
            {
            }
        }

        private sealed class QueuedSource : ICallAudioSource
        {
            private readonly ConcurrentQueue<float[]> _frames = new ConcurrentQueue<float[]>();

            public void Push(float[] frame) => _frames.Enqueue(frame);

            public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public float[] TryReadFrame()
            {
                float[] frame;
                return _frames.TryDequeue(out frame) ? frame : null;
            }

            public Task<float[]> ReadAsync(CancellationToken cancellationToken) =>
                Task.FromResult(TryReadFrame());

            public void Dispose()
            {
            }
        }
    }
}
