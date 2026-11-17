using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Unison.Voip.Engine.Relay;
using Unison.Voip.Engine.Relay.Sctp;
using Xunit;

namespace Unison.Core.Tests.Voip.Relay
{
    public sealed class SctpAssociationTests
    {
        [Fact]
        public void Crc32c_matches_empty_and_known_vector()
        {
            // RFC 3720 appendix B style empty / "123456789" Castagnoli checks.
            Assert.Equal(0u, SctpCrc32c.Compute(new byte[0], 0, 0));
            Assert.Equal(0xE3069283u, SctpCrc32c.Compute(Encoding.ASCII.GetBytes("123456789"), 0, 9));
        }

        [Fact]
        public async Task Client_and_server_handshake_then_exchange_binary_datachannel_messages()
        {
            MemoryBytePipe.Pair pipes = MemoryBytePipe.Create();
            var server = new SctpAssociation(pipes.Right, isClient: false);
            var client = new SctpAssociation(pipes.Left, isClient: true);

            Task serverReady = server.AcceptAsync(CancellationToken.None);
            await client.ConnectAsync(CancellationToken.None);
            await serverReady;

            DataChannel clientDc = client.OpenDataChannel(0, RelayPacketClassifier.DataChannelLabel);
            DataChannel serverDc = server.OpenDataChannel(0, RelayPacketClassifier.DataChannelLabel);

            byte[] payload = { 0x00, 0x01, 0x02, 0x03, 0x04 };
            await clientDc.SendAsync(payload, CancellationToken.None);

            byte[] got = await serverDc.ReceiveAsync(CancellationToken.None);
            Assert.Equal(payload, got);

            await serverDc.SendAsync(Encoding.ASCII.GetBytes("pong"), CancellationToken.None);
            byte[] back = await clientDc.ReceiveAsync(CancellationToken.None);
            Assert.Equal(Encoding.ASCII.GetBytes("pong"), back);

            client.Dispose();
            server.Dispose();
        }

        [Fact]
        public void Encode_init_has_common_header_and_init_chunk()
        {
            byte[] packet = SctpPacket.EncodeInit(
                sourcePort: 5000,
                destPort: 5000,
                initiateTag: 0x11223344,
                aRwnd: 262144,
                outboundStreams: 16,
                inboundStreams: 16,
                initialTsn: 1);

            Assert.True(packet.Length >= 12 + 20);
            Assert.Equal(5000, (packet[0] << 8) | packet[1]);
            Assert.Equal(5000, (packet[2] << 8) | packet[3]);
            Assert.Equal(0, packet[4]); // verification tag 0 on INIT
            Assert.Equal(0, packet[5]);
            Assert.Equal(0, packet[6]);
            Assert.Equal(0, packet[7]);
            Assert.Equal(SctpChunkType.Init, packet[12]);
        }
    }

    /// <summary>In-memory duplex for SCTP without DTLS.</summary>
    internal sealed class MemoryBytePipe : IByteTransport
    {
        private readonly ConcurrentQueue<byte[]> _inbox = new ConcurrentQueue<byte[]>();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private readonly CancellationTokenSource _closed = new CancellationTokenSource();
        private MemoryBytePipe _peer;

        public static Pair Create()
        {
            var left = new MemoryBytePipe();
            var right = new MemoryBytePipe();
            left._peer = right;
            right._peer = left;
            return new Pair(left, right);
        }

        public sealed class Pair
        {
            public Pair(IByteTransport left, IByteTransport right)
            {
                Left = left;
                Right = right;
            }

            public IByteTransport Left { get; }

            public IByteTransport Right { get; }
        }

        public void Send(byte[] data, int offset, int length)
        {
            var copy = new byte[length];
            Buffer.BlockCopy(data, offset, copy, 0, length);
            _peer._inbox.Enqueue(copy);
            _peer._signal.Release();
        }

        public async Task<byte[]> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (_closed.IsCancellationRequested)
            {
                return null;
            }

            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                       cancellationToken, _closed.Token))
            {
                try
                {
                    await _signal.WaitAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_closed.IsCancellationRequested)
                {
                    return null;
                }
            }

            byte[] next;
            return _inbox.TryDequeue(out next) ? next : null;
        }

        public void Dispose()
        {
            if (!_closed.IsCancellationRequested)
            {
                _closed.Cancel();
            }
        }
    }
}
