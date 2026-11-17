// =============================================================================
// LoopbackUdpTransport
//
// In-memory UDP pipe for netstandard KAT — proves send/recv before DatagramSocket.
// =============================================================================
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Unison.Voip.Abstractions;

namespace Unison.Voip.Null
{
    public sealed class LoopbackUdpTransport : ICallUdpTransport
    {
        private readonly ConcurrentQueue<CallUdpDatagram> _inbox =
            new ConcurrentQueue<CallUdpDatagram>();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private readonly CancellationTokenSource _closed = new CancellationTokenSource();
        private volatile bool _bound;

        public int SentCount { get; private set; }

        public CallUdpDatagram LastSent { get; private set; }

        public Task BindAsync(CancellationToken cancellationToken)
        {
            ThrowIfClosed();
            _bound = true;
            return Task.CompletedTask;
        }

        public Task SendAsync(byte[] payload, IPEndPoint remote, CancellationToken cancellationToken)
        {
            ThrowIfClosed();
            if (!_bound)
            {
                throw new InvalidOperationException("UDP transport is not bound.");
            }

            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            var copy = new byte[payload.Length];
            Buffer.BlockCopy(payload, 0, copy, 0, payload.Length);
            var datagram = new CallUdpDatagram
            {
                Payload = copy,
                Remote = remote
            };
            LastSent = datagram;
            SentCount++;
            _inbox.Enqueue(datagram);
            _signal.Release();
            return Task.CompletedTask;
        }

        public async Task<CallUdpDatagram> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (_closed.IsCancellationRequested)
            {
                return null;
            }

            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                       cancellationToken,
                       _closed.Token))
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

        private void ThrowIfClosed()
        {
            if (_closed.IsCancellationRequested)
            {
                throw new ObjectDisposedException(nameof(LoopbackUdpTransport));
            }
        }
    }
}
