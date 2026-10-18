using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using Windows.Web;
using Unison.Core.Contracts;
using Unison.Uwp.Services;

namespace Unison.Uwp.Transport
{
    internal sealed class MessageWebSocketTransport : IWhatsAppTransport
    {
        private MessageWebSocket _socket;
        private DataWriter _writer;
        private bool _connected;
        private int _connectCompleted;
        private TaskCompletionSource<bool> _connectAborted;
        private readonly System.Threading.SemaphoreSlim _sendLock = new System.Threading.SemaphoreSlim(1, 1);
        private readonly System.Threading.SemaphoreSlim _receiveOrder = new System.Threading.SemaphoreSlim(1, 1);

        public string Name => "MessageWebSocket-classic";
        public bool IsConnected => _connected;
        public bool IsOwnedByBroker => false;
        public string SocketId => string.Empty;

        public event Func<object, TransportMessageEventArgs, Task> MessageReceived;
        public event EventHandler<TransportClosedEventArgs> Closed;

        public async Task ConnectAsync(Uri uri, IDictionary<string, string> headers)
        {
            if (uri == null) throw new ArgumentNullException(nameof(uri));

            _socket = new MessageWebSocket();
            _socket.Control.MessageType = SocketMessageType.Binary;
            // Default is small on some Mobile builds; Noise + offline frames can exceed it.
            try
            {
                _socket.Control.MaxMessageSize = 4 * 1024 * 1024;
            }
            catch
            {
            }

            ApplyRequestHeaders(headers);
            _socket.MessageReceived += OnMessageReceived;
            _socket.Closed += OnClosed;

            RuntimeDiagnosticsService.Instance.Write(
                "transport",
                "messagewebsocket-connect-begin",
                "host=" + uri.Host);

            // Same WinRT call as 1.8. On Creators Update the abort arrives on this callback
            // while ConnectAsync stays pending (1.9.9). Resume inline on that callback.
            // RunContinuationsAsynchronously posted the resume onto a thread that does not
            // run while the WinRT connect is outstanding, so connecting stayed true.
            var aborted = new TaskCompletionSource<bool>();
            _connectAborted = aborted;
            Task connect = _socket.ConnectAsync(uri).AsTask();
            Task winner = await Task.WhenAny(connect, aborted.Task).ConfigureAwait(false);
            RuntimeDiagnosticsService.Instance.Write(
                "transport",
                "messagewebsocket-connect-released",
                "winner=" + (ReferenceEquals(winner, connect) ? "connect" : "abort"));
            if (!ReferenceEquals(winner, connect))
            {
                throw new IOException(
                    "MessageWebSocket connection aborted before connect completed");
            }

            await connect.ConfigureAwait(false);
            Interlocked.Exchange(ref _connectCompleted, 1);
            _writer = new DataWriter(_socket.OutputStream);
            _connected = true;

            RuntimeDiagnosticsService.Instance.Write(
                "transport",
                "messagewebsocket-connected",
                "host=" + uri.Host);
        }

        private void ApplyRequestHeaders(IDictionary<string, string> headers)
        {
            if (_socket == null)
            {
                return;
            }

            // Restricted headers throw on SetRequestHeader — apply one-by-one.
            if (headers == null)
            {
                return;
            }

            foreach (var pair in headers)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value == null)
                {
                    continue;
                }

                try
                {
                    _socket.SetRequestHeader(pair.Key, pair.Value);
                }
                catch (Exception ex)
                {
                    RuntimeDiagnosticsService.Instance.Write(
                        "transport",
                        "messagewebsocket-header-skipped",
                        "name=" + pair.Key +
                        "; error=" + ex.GetType().Name);
                }
            }
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
                    uint length = reader.UnconsumedBufferLength;
                    if (length == 0)
                    {
                        return;
                    }

                    bytes = new byte[length];
                    reader.ReadBytes(bytes);
                }

                // MessageReceived runs on the UI thread. History apply then blocks that
                // thread with GetResult (1.8 kept this on a background pipeline). Copy the
                // bytes here, return, and process one frame at a time off the UI thread.
                byte[] payload = bytes;
                _ = Task.Run(async () =>
                {
                    await _receiveOrder.WaitAsync();
                    try
                    {
                        await RaiseMessageReceivedAsync(payload);
                    }
                    catch (Exception ex)
                    {
                        RuntimeDiagnosticsService.Instance.RecordException(
                            "transport",
                            "messagewebsocket-frame-failed",
                            ex,
                            "bytes=" + payload.Length);
                    }
                    finally
                    {
                        _receiveOrder.Release();
                    }
                });
            }
            catch (Exception ex)
            {
                // GetDataReader throws when the TCP connection was aborted (often without Closed).
                WebErrorStatus status = WebErrorStatus.Unknown;
                try
                {
                    status = WebSocketError.GetStatus(ex.HResult);
                }
                catch
                {
                }

                RuntimeDiagnosticsService.Instance.RecordException(
                    "transport",
                    "messagewebsocket-receive-failed",
                    ex,
                    "webStatus=" + status +
                    "; hresult=0x" + ex.HResult.ToString("X8") +
                    "; connectCompleted=" + (Interlocked.CompareExchange(ref _connectCompleted, 0, 0) != 0));

                // Always dispose: if abort arrives before ConnectAsync returns, leaving the
                // socket alive leaves session.ConnectAsync hung forever (1.9.7).
                _connected = false;
                AbortSocket("receive-aborted:" + status);
                Closed?.Invoke(this, new TransportClosedEventArgs
                {
                    Code = 1006,
                    Reason = "MessageWebSocket receive failure: " + status,
                    Error = ex
                });
            }
        }

        private void OnClosed(IWebSocket sender, WebSocketClosedEventArgs args)
        {
            _connected = false;
            if (Interlocked.CompareExchange(ref _connectCompleted, 0, 0) == 0)
            {
                _connectAborted?.TrySetResult(true);
            }

            Closed?.Invoke(this, new TransportClosedEventArgs
            {
                Code = args.Code,
                Reason = args.Reason
            });
        }

        private void AbortSocket(string reason)
        {
            MessageWebSocket socket = _socket;
            if (socket == null)
            {
                return;
            }

            _connectAborted?.TrySetResult(true);
            RuntimeDiagnosticsService.Instance.Write(
                "transport",
                "messagewebsocket-abort",
                "reason=" + (reason ?? string.Empty));

            try
            {
                socket.MessageReceived -= OnMessageReceived;
                socket.Closed -= OnClosed;
            }
            catch
            {
            }

            try
            {
                socket.Dispose();
            }
            catch
            {
            }

            if (ReferenceEquals(_socket, socket))
            {
                _socket = null;
            }
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
