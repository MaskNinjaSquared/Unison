using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

using Unison.Core.Contracts;

namespace Unison.Uwp.Transport
{
    internal sealed class MessageWebSocketTransport : IWhatsAppTransport
    {
        private MessageWebSocket _socket;
        private DataWriter _writer;
        private bool _connected;
        private readonly System.Threading.SemaphoreSlim _sendLock = new System.Threading.SemaphoreSlim(1, 1);

        public string Name => "MessageWebSocket-classic";
        public bool IsConnected => _connected;
        public bool IsOwnedByBroker => false;
        public string SocketId => string.Empty;

        public event Func<object, TransportMessageEventArgs, Task> MessageReceived;
        public event EventHandler<TransportClosedEventArgs> Closed;

        public async Task ConnectAsync(Uri uri, IDictionary<string, string> headers)
        {
            _socket = new MessageWebSocket();
            _socket.Control.MessageType = SocketMessageType.Binary;
            if (headers != null)
            {
                foreach (var pair in headers)
                {
                    _socket.SetRequestHeader(pair.Key, pair.Value);
                }
            }
            _socket.MessageReceived += OnMessageReceived;
            _socket.Closed += OnClosed;
            await _socket.ConnectAsync(uri);
            _writer = new DataWriter(_socket.OutputStream);
            _connected = true;
        }

        public async Task SendAsync(byte[] data)
        {
            if (!_connected || _writer == null)
            {
                throw new InvalidOperationException("Transport is not connected");
            }
            await _sendLock.WaitAsync();
            try
            {
                _writer.WriteBytes(data ?? new byte[0]);
                await _writer.StoreAsync();
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public Task<bool> TransferToBrokerAsync(
            string reason,
            Func<string, Task> beforeTransfer)
        {
            return Task.FromResult(false);
        }

        public Task<bool> ReclaimFromBrokerAsync()
        {
            return Task.FromResult(false);
        }

        public Task CloseAsync(ushort code, string reason)
        {
            _connected = false;
            try { _socket?.Close(code, reason ?? string.Empty); } catch { }
            return Task.CompletedTask;
        }

        private async void OnMessageReceived(MessageWebSocket sender, MessageWebSocketMessageReceivedEventArgs args)
        {
            try
            {
                byte[] bytes;
                using (var reader = args.GetDataReader())
                {
                    bytes = new byte[reader.UnconsumedBufferLength];
                    reader.ReadBytes(bytes);
                }
                await RaiseMessageReceivedAsync(bytes);
            }
            catch (Exception ex)
            {
                Closed?.Invoke(this, new TransportClosedEventArgs
                {
                    Code = 1006,
                    Reason = "MessageWebSocket receive failure",
                    Error = ex
                });
            }
        }

        private void OnClosed(IWebSocket sender, WebSocketClosedEventArgs args)
        {
            _connected = false;
            Closed?.Invoke(this, new TransportClosedEventArgs
            {
                Code = args.Code,
                Reason = args.Reason
            });
        }

        private async Task RaiseMessageReceivedAsync(byte[] data)
        {
            var handler = MessageReceived;
            if (handler == null) return;
            var args = new TransportMessageEventArgs { Data = data };
            foreach (var subscriber in handler.GetInvocationList())
            {
                var asyncHandler = subscriber as Func<object, TransportMessageEventArgs, Task>;
                if (asyncHandler != null)
                {
                    await asyncHandler(this, args);
                }
            }
        }

        public void Dispose()
        {
            _connected = false;
            if (_socket != null)
            {
                _socket.MessageReceived -= OnMessageReceived;
                _socket.Closed -= OnClosed;
            }
            try { _writer?.Dispose(); } catch { }
            try { _socket?.Dispose(); } catch { }
            _writer = null;
            _socket = null;
            _sendLock.Dispose();
        }
    }
}
