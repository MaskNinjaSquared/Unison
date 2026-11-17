// =============================================================================
// DataChannel
//
// Pre-negotiated WebRTC DataChannel (id + label). No DCEP OPEN/ACK — WA uses
// Negotiated=true. Binary messages use PPID 53.
// =============================================================================
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Unison.Voip.Engine.Relay.Sctp
{
    public sealed class DataChannel
    {
        private readonly SctpAssociation _association;
        private readonly ConcurrentQueue<byte[]> _inbox = new ConcurrentQueue<byte[]>();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private readonly CancellationTokenSource _closed = new CancellationTokenSource();

        internal DataChannel(SctpAssociation association, ushort streamId, string label)
        {
            _association = association ?? throw new ArgumentNullException(nameof(association));
            StreamId = streamId;
            Label = label ?? string.Empty;
        }

        public ushort StreamId { get; }

        public string Label { get; }

        public Task SendAsync(byte[] payload, CancellationToken cancellationToken)
        {
            ThrowIfClosed();
            return _association.SendDataAsync(StreamId, payload, cancellationToken);
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

        internal void Enqueue(byte[] payload)
        {
            if (_closed.IsCancellationRequested || payload == null)
            {
                return;
            }

            _inbox.Enqueue(payload);
            try
            {
                _signal.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        internal void Close()
        {
            if (!_closed.IsCancellationRequested)
            {
                _closed.Cancel();
            }

            try
            {
                _signal.Release();
            }
            catch
            {
            }
        }

        private void ThrowIfClosed()
        {
            if (_closed.IsCancellationRequested)
            {
                throw new ObjectDisposedException(nameof(DataChannel));
            }
        }
    }
}
