// =============================================================================
// UwpDatagramCallUdpTransport
//
// Creators/16299-safe DatagramSocket behind ICallUdpTransport.
// =============================================================================
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Unison.Voip.Abstractions;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace Unison.Uwp.Services.Calls
{
    internal sealed class UwpDatagramCallUdpTransport : ICallUdpTransport
    {
        private readonly ConcurrentQueue<CallUdpDatagram> _inbox =
            new ConcurrentQueue<CallUdpDatagram>();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private readonly CancellationTokenSource _closed = new CancellationTokenSource();
        private readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);
        private DatagramSocket _socket;
        private IPEndPoint _streamRemote;
        private IOutputStream _stream;

        public async Task BindAsync(CancellationToken cancellationToken)
        {
            ThrowIfClosed();
            if (_socket != null)
            {
                return;
            }

            var socket = new DatagramSocket();
            socket.MessageReceived += OnMessageReceived;
            // Any free local port; the relay learns our mapped address from traffic.
            await socket.BindServiceNameAsync(string.Empty).AsTask(cancellationToken)
                .ConfigureAwait(false);
            _socket = socket;
        }

        public async Task SendAsync(
            byte[] payload,
            IPEndPoint remote,
            CancellationToken cancellationToken)
        {
            ThrowIfClosed();
            if (_socket == null)
            {
                throw new InvalidOperationException("DatagramSocket is not bound.");
            }

            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            if (remote == null)
            {
                throw new ArgumentNullException(nameof(remote));
            }

            await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                IOutputStream stream = await GetStreamAsync(remote, cancellationToken)
                    .ConfigureAwait(false);
                await stream.WriteAsync(payload.AsBuffer()).AsTask(cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _sendGate.Release();
            }
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
            if (_closed.IsCancellationRequested)
            {
                return;
            }

            _closed.Cancel();
            DatagramSocket socket = _socket;
            _socket = null;
            IOutputStream stream = _stream;
            _stream = null;
            _streamRemote = null;
            try
            {
                stream?.Dispose();
            }
            catch
            {
            }

            if (socket != null)
            {
                try
                {
                    socket.MessageReceived -= OnMessageReceived;
                    socket.Dispose();
                }
                catch
                {
                }
            }
        }

        /// <summary>One output stream per remote; a call talks to a single relay.</summary>
        private async Task<IOutputStream> GetStreamAsync(
            IPEndPoint remote,
            CancellationToken cancellationToken)
        {
            if (_stream != null && remote.Equals(_streamRemote))
            {
                return _stream;
            }

            IOutputStream previous = _stream;
            _stream = await _socket
                .GetOutputStreamAsync(
                    new HostName(remote.Address.ToString()),
                    remote.Port.ToString())
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            _streamRemote = remote;
            previous?.Dispose();
            return _stream;
        }

        private void OnMessageReceived(
            DatagramSocket sender,
            DatagramSocketMessageReceivedEventArgs args)
        {
            if (_closed.IsCancellationRequested)
            {
                return;
            }

            try
            {
                DataReader reader = args.GetDataReader();
                uint len = reader.UnconsumedBufferLength;
                var payload = new byte[len];
                if (len > 0)
                {
                    reader.ReadBytes(payload);
                }

                IPAddress address;
                if (!IPAddress.TryParse(args.RemoteAddress.RawName, out address))
                {
                    address = IPAddress.Any;
                }

                int port;
                if (!int.TryParse(args.RemotePort, out port))
                {
                    port = 0;
                }

                _inbox.Enqueue(new CallUdpDatagram
                {
                    Payload = payload,
                    Remote = new IPEndPoint(address, port)
                });
                _signal.Release();
            }
            catch
            {
                // A malformed datagram must not tear down the receive path.
            }
        }

        private void ThrowIfClosed()
        {
            if (_closed.IsCancellationRequested)
            {
                throw new ObjectDisposedException(nameof(UwpDatagramCallUdpTransport));
            }
        }
    }
}
