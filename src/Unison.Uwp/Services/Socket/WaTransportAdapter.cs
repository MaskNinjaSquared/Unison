// =============================================================================
// WaTransportAdapter
//
// Bridges the app's existing IWhatsAppTransport onto Unison.Socket's IWaTransport.
//
// The two interfaces are the same shape but carry different EventArgs types.
// Adapting here rather than changing either one is what lets the new stack run on
// the battle-tested UWP transports without editing a line of the code the shipped
// app depends on.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Socket.Abstractions;

namespace Unison.Uwp.Services.Socket
{
    internal sealed class WaTransportAdapter : IWaTransport
    {
        private readonly IWhatsAppTransport _inner;
        private bool _disposed;

        public WaTransportAdapter(IWhatsAppTransport inner)
        {
            if (inner == null)
            {
                throw new ArgumentNullException(nameof(inner));
            }

            _inner = inner;
            _inner.MessageReceived += OnInnerMessageReceived;
            _inner.Closed += OnInnerClosed;
        }

        public event Func<object, WaTransportMessageEventArgs, Task> MessageReceived;

        public event EventHandler<WaTransportClosedEventArgs> Closed;

        public string Name
        {
            get { return _inner.Name; }
        }

        public bool IsConnected
        {
            get { return _inner.IsConnected; }
        }

        public bool IsOwnedByBroker
        {
            get { return _inner.IsOwnedByBroker; }
        }

        public string SocketId
        {
            get { return _inner.SocketId; }
        }

        public Task ConnectAsync(Uri uri, IDictionary<string, string> headers)
        {
            return _inner.ConnectAsync(uri, headers);
        }

        public Task SendAsync(byte[] data)
        {
            return _inner.SendAsync(data);
        }

        public Task CloseAsync(ushort code, string reason)
        {
            return _inner.CloseAsync(code, reason);
        }

        public Task<bool> TransferToBrokerAsync(string reason, Func<string, Task> beforeTransfer)
        {
            return _inner.TransferToBrokerAsync(reason, beforeTransfer);
        }

        public Task<bool> ReclaimFromBrokerAsync()
        {
            return _inner.ReclaimFromBrokerAsync();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _inner.MessageReceived -= OnInnerMessageReceived;
            _inner.Closed -= OnInnerClosed;
            _inner.Dispose();
        }

        private Task OnInnerMessageReceived(object sender, TransportMessageEventArgs args)
        {
            var handler = MessageReceived;
            if (handler == null)
            {
                return Task.FromResult(true);
            }

            return handler(
                this,
                new WaTransportMessageEventArgs
                {
                    Data = args != null ? args.Data : null,
                    IsBrokerReplay = args != null && args.IsBrokerReplay
                });
        }

        private void OnInnerClosed(object sender, TransportClosedEventArgs args)
        {
            var handler = Closed;
            if (handler == null)
            {
                return;
            }

            handler(
                this,
                new WaTransportClosedEventArgs
                {
                    Code = args != null ? args.Code : (ushort)0,
                    Reason = args != null ? args.Reason : null,
                    Error = args != null ? args.Error : null
                });
        }
    }
}
