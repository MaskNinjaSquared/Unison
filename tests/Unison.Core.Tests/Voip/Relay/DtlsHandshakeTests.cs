using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Unison.Voip.Abstractions;
using Unison.Voip.Engine.Relay;
using Unison.Voip.Null;
using Xunit;

namespace Unison.Core.Tests.Voip.Relay
{
    public sealed class DtlsHandshakeTests
    {
        [Fact]
        public async Task Client_and_server_complete_dtls12_handshake_over_cross_linked_udp()
        {
            CrossLinkedUdpPair pair = CrossLinkedUdpPair.Create();
            await pair.Client.BindAsync(CancellationToken.None);
            await pair.Server.BindAsync(CancellationToken.None);

            var remote = new IPEndPoint(IPAddress.Loopback, 3478);
            Task<Org.BouncyCastle.Tls.DtlsTransport> serverTask = Task.Run(() =>
                DtlsRelayConnector.AcceptServer(pair.Server, remote, TimeSpan.FromSeconds(8)));
            Org.BouncyCastle.Tls.DtlsTransport client = await Task.Run(() =>
                DtlsRelayConnector.ConnectClient(pair.Client, remote, TimeSpan.FromSeconds(8)));
            Org.BouncyCastle.Tls.DtlsTransport server = await serverTask;

            byte[] hello = Encoding.ASCII.GetBytes("wa-voip-dtls");
            client.Send(hello, 0, hello.Length);

            var buf = new byte[256];
            int n = server.Receive(buf, 0, buf.Length, 3000);
            Assert.Equal(hello.Length, n);
            Assert.Equal(hello, Slice(buf, n));

            client.Close();
            server.Close();
        }

        [Fact]
        public void UdpDatagramTransport_send_and_receive_round_trip()
        {
            CrossLinkedUdpPair pair = CrossLinkedUdpPair.Create();
            pair.Client.BindAsync(CancellationToken.None).GetAwaiter().GetResult();
            pair.Server.BindAsync(CancellationToken.None).GetAwaiter().GetResult();

            var remote = new IPEndPoint(IPAddress.Loopback, 1);
            using (var a = new UdpDatagramTransport(pair.Client, remote))
            using (var b = new UdpDatagramTransport(pair.Server, remote))
            {
                a.StartPump();
                b.StartPump();

                byte[] payload = { 1, 2, 3, 4 };
                a.Send(payload, 0, payload.Length);

                var buf = new byte[16];
                int n = b.Receive(buf, 0, buf.Length, 2000);
                Assert.Equal(4, n);
                Assert.Equal(payload, Slice(buf, n));
            }
        }

        private static byte[] Slice(byte[] buf, int n)
        {
            var copy = new byte[n];
            Buffer.BlockCopy(buf, 0, copy, 0, n);
            return copy;
        }
    }

    /// <summary>Two UDP transports that deliver each other's Send into the peer inbox.</summary>
    internal sealed class CrossLinkedUdpPair
    {
        public ICallUdpTransport Client { get; }

        public ICallUdpTransport Server { get; }

        private CrossLinkedUdpPair(ICallUdpTransport client, ICallUdpTransport server)
        {
            Client = client;
            Server = server;
        }

        public static CrossLinkedUdpPair Create()
        {
            var left = new InboxUdpTransport();
            var right = new InboxUdpTransport();
            left.Peer = right;
            right.Peer = left;
            return new CrossLinkedUdpPair(left, right);
        }

        private sealed class InboxUdpTransport : ICallUdpTransport
        {
            private readonly System.Collections.Concurrent.ConcurrentQueue<CallUdpDatagram> _inbox =
                new System.Collections.Concurrent.ConcurrentQueue<CallUdpDatagram>();
            private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
            private readonly CancellationTokenSource _closed = new CancellationTokenSource();
            private bool _bound;

            public InboxUdpTransport Peer { get; set; }

            public Task BindAsync(CancellationToken cancellationToken)
            {
                _bound = true;
                return Task.CompletedTask;
            }

            public Task SendAsync(byte[] payload, IPEndPoint remote, CancellationToken cancellationToken)
            {
                if (!_bound || _closed.IsCancellationRequested)
                {
                    throw new ObjectDisposedException(nameof(InboxUdpTransport));
                }

                var copy = new byte[payload.Length];
                Buffer.BlockCopy(payload, 0, copy, 0, payload.Length);
                Peer._inbox.Enqueue(new CallUdpDatagram { Payload = copy, Remote = remote });
                Peer._signal.Release();
                return Task.CompletedTask;
            }

            public async Task<CallUdpDatagram> ReceiveAsync(CancellationToken cancellationToken)
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

                CallUdpDatagram next;
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
}
