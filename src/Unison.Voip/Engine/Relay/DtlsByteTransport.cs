// =============================================================================
// DtlsByteTransport
//
// Adapts BouncyCastle DtlsTransport (sync Send/Receive) to IByteTransport for
// the SCTP association. A pump thread fills the receive queue.
// =============================================================================
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Tls;
using Unison.Voip.Engine.Relay.Sctp;

namespace Unison.Voip.Engine.Relay
{
    public sealed class DtlsByteTransport : IByteTransport
    {
        private readonly DtlsTransport _dtls;
        private readonly ConcurrentQueue<byte[]> _inbox = new ConcurrentQueue<byte[]>();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private readonly CancellationTokenSource _pumpCts = new CancellationTokenSource();
        private readonly object _sendGate = new object();
        private readonly Task _pump;
        private volatile bool _closed;

        public DtlsByteTransport(DtlsTransport dtls)
        {
            _dtls = dtls ?? throw new ArgumentNullException(nameof(dtls));
            _pump = Task.Run((Action)Pump);
        }

        public void Send(byte[] data, int offset, int length)
        {
            ThrowIfClosed();
            lock (_sendGate)
            {
                _dtls.Send(data, offset, length);
            }
        }

        public async Task<byte[]> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (_closed)
            {
                return null;
            }

            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                       cancellationToken, _pumpCts.Token))
            {
                try
                {
                    await _signal.WaitAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }

            byte[] next;
            return _inbox.TryDequeue(out next) ? next : null;
        }

        public void Dispose()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            try
            {
                _pumpCts.Cancel();
            }
            catch
            {
            }

            try
            {
                _dtls.Close();
            }
            catch
            {
            }

            try
            {
                _signal.Release();
            }
            catch
            {
            }
        }

        private void Pump()
        {
            var buf = new byte[UdpDatagramTransport.DefaultMtu];
            while (!_closed && !_pumpCts.IsCancellationRequested)
            {
                int n;
                try
                {
                    n = _dtls.Receive(buf, 0, buf.Length, 500);
                }
                catch
                {
                    break;
                }

                if (n <= 0)
                {
                    continue;
                }

                var copy = new byte[n];
                Buffer.BlockCopy(buf, 0, copy, 0, n);
                _inbox.Enqueue(copy);
                try
                {
                    _signal.Release();
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
            }
        }

        private void ThrowIfClosed()
        {
            if (_closed)
            {
                throw new ObjectDisposedException(nameof(DtlsByteTransport));
            }
        }
    }
}
