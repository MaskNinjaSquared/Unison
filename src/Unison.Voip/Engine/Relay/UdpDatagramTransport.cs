// =============================================================================
// UdpDatagramTransport
//
// Bridges async ICallUdpTransport to BouncyCastle's synchronous DatagramTransport
// for DTLS. A background pump fills a receive queue; Receive waits with a timeout.
// =============================================================================
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Tls;
using Unison.Voip.Abstractions;

namespace Unison.Voip.Engine.Relay
{
    public sealed class UdpDatagramTransport : DatagramTransport, IDisposable
    {
        public const int DefaultMtu = 1200;

        private readonly ICallUdpTransport _udp;
        private readonly IPEndPoint _remote;
        private readonly ConcurrentQueue<byte[]> _inbox = new ConcurrentQueue<byte[]>();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private readonly CancellationTokenSource _pumpCts = new CancellationTokenSource();
        private readonly object _sendGate = new object();
        private Task _pump;
        private volatile bool _closed;

        public UdpDatagramTransport(ICallUdpTransport udp, IPEndPoint remote)
        {
            _udp = udp ?? throw new ArgumentNullException(nameof(udp));
            _remote = remote ?? throw new ArgumentNullException(nameof(remote));
        }

        /// <summary>Starts the async receive pump. Call after BindAsync on the UDP socket.</summary>
        public void StartPump()
        {
            if (_pump != null || _closed)
            {
                return;
            }

            _pump = Task.Run(() => PumpAsync(_pumpCts.Token));
        }

        public int GetReceiveLimit() => DefaultMtu;

        public int GetSendLimit() => DefaultMtu;

        public void Send(byte[] buf, int off, int len)
        {
            ThrowIfClosed();
            if (buf == null)
            {
                throw new ArgumentNullException(nameof(buf));
            }

            var payload = new byte[len];
            Buffer.BlockCopy(buf, off, payload, 0, len);
            lock (_sendGate)
            {
                _udp.SendAsync(payload, _remote, CancellationToken.None).GetAwaiter().GetResult();
            }
        }

        public int Receive(byte[] buf, int off, int len, int waitMillis)
        {
            ThrowIfClosed();
            if (waitMillis < 0)
            {
                waitMillis = 0;
            }

            try
            {
                if (!_signal.Wait(waitMillis))
                {
                    return -1;
                }
            }
            catch (ObjectDisposedException)
            {
                return -1;
            }

            if (_closed)
            {
                return -1;
            }

            byte[] packet;
            if (!_inbox.TryDequeue(out packet) || packet == null)
            {
                return -1;
            }

            int copy = Math.Min(len, packet.Length);
            Buffer.BlockCopy(packet, 0, buf, off, copy);
            return copy;
        }

        public void Close() => Dispose();

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
                _signal.Release();
            }
            catch
            {
            }

            _pumpCts.Dispose();
            _signal.Dispose();
        }

        private async Task PumpAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && !_closed)
            {
                CallUdpDatagram datagram;
                try
                {
                    datagram = await _udp.ReceiveAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    break;
                }

                if (datagram == null || datagram.Payload == null)
                {
                    break;
                }

                _inbox.Enqueue(datagram.Payload);
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
                throw new ObjectDisposedException(nameof(UdpDatagramTransport));
            }
        }
    }
}
