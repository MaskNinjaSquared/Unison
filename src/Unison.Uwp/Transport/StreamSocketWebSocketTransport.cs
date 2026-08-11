using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using Unison.Background;
using Unison.Uwp.Services;

using Unison.Core.Contracts;

namespace Unison.Uwp.Transport
{
    internal sealed class StreamSocketWebSocketTransport : IWhatsAppTransport
    {
        private readonly SemaphoreSlim _ownershipLock = new SemaphoreSlim(1, 1);
        private StreamSocket _socket;
        private RawWebSocketConnection _connection;
        private CancellationTokenSource _receiveCts;
        private Task _receiveTask;
        private bool _connected;
        private bool _ownedByBroker;
        private bool _transferring;
        private bool _disposed;
        private string _socketId;
        private readonly string _generationId;
        private BrokerOwnershipState _ownershipState;
        private long _activityCount;

        public string Name => "StreamSocket-RFC6455-broker";
        public bool IsConnected => _connected;
        public bool IsOwnedByBroker => _ownedByBroker;
        public string SocketId => _socketId ?? string.Empty;
        public long ActivityCount => Interlocked.Read(ref _activityCount);

        public event Func<object, TransportMessageEventArgs, Task> MessageReceived;
        public event EventHandler<TransportClosedEventArgs> Closed;

        internal StreamSocketWebSocketTransport()
            : this(null)
        {
        }

        internal StreamSocketWebSocketTransport(string restoredSocketId)
        {
            _socketId = BrokerOwnershipStore.IsManagedSocketId(restoredSocketId)
                ? restoredSocketId
                : null;
            _generationId = Guid.NewGuid().ToString("N");
        }

        public async Task ConnectAsync(Uri uri, IDictionary<string, string> headers)
        {
            if (uri == null) throw new ArgumentNullException(nameof(uri));
            ThrowIfDisposed();

            BrokerInterprocessLease lease = await BrokerInterprocessLock.AcquireAsync(
                "foreground:connect",
                TimeSpan.FromSeconds(8),
                CancellationToken.None);
            if (lease == null)
            {
                throw new TimeoutException(
                    "Timed out waiting for Socket Broker ownership before connect");
            }

            try
            {
                bool brokerReady = await SocketBrokerCoordinator.Instance.EnsureReadyAsync();
                RuntimeDiagnosticsService.Instance.Write(
                    "transport",
                    "streamsocket-connect-start",
                    "host=" + uri.Host + "; brokerReady=" + brokerReady);

                if (!brokerReady)
                {
                    throw new InvalidOperationException(
                        "SocketActivityTrigger background task is not available");
                }

                // A fresh connection invalidates any previously persisted broker Noise state.
                await NoiseSessionStore.ClearAsync();
                await BackgroundSignalSnapshotStore.ClearAsync();
                await BackgroundDisplayNameStore.ClearAsync();

                // A cold launch cannot reuse the Noise state of a socket left by a terminated
                // process. Remove any stale broker ownership before creating a fresh session.
                await SocketBrokerCoordinator.DisposeBrokerSocketAsync();
                await BrokerFrameJournal.ClearAsync();
                await BrokerLog.AppendAsync(
                    "journal",
                    "journal-cleared reason=fresh-connection");

                _socketId = BrokerOwnershipStore.CreateSocketId();
                _ownershipState = BrokerOwnershipStore.Create(
                    _socketId,
                    _generationId,
                    "foreground",
                    "connect-start");

                _socket = new StreamSocket();
                _socket.Control.KeepAlive = true;
                _socket.Control.NoDelay = true;

                if (brokerReady)
                {
                    try
                    {
                        _socket.EnableTransferOwnership(
                            SocketBrokerCoordinator.Instance.TaskId,
                            SocketActivityConnectedStandbyAction.Wake);
                        RuntimeDiagnosticsService.Instance.Write(
                            "socket-broker",
                            "ownership-enabled",
                            "mode=Wake");
                    }
                    catch (Exception missingTaskError)
                        when (IsElementNotFound(missingTaskError))
                    {
                        RuntimeDiagnosticsService.Instance.RecordException(
                            "socket-broker",
                            "ownership-taskid-invalid",
                            missingTaskError);

                        bool recreated = await SocketBrokerCoordinator.Instance
                            .RecreateRegistrationAsync(
                                "enable-ownership-element-not-found");
                        if (!recreated)
                        {
                            throw;
                        }

                        RuntimeDiagnosticsService.Instance.Write(
                            "socket-broker",
                            "ownership-enable-retry",
                            "mode=Wake; taskId=" +
                            SocketBrokerCoordinator.Instance.TaskId);
                        try
                        {
                            _socket.EnableTransferOwnership(
                                SocketBrokerCoordinator.Instance.TaskId,
                                SocketActivityConnectedStandbyAction.Wake);
                            RuntimeDiagnosticsService.Instance.Write(
                                "socket-broker",
                                "ownership-enabled",
                                "mode=Wake; registrationRecreated=True");
                        }
                        catch (Exception retryError)
                        {
                            RuntimeDiagnosticsService.Instance.RecordException(
                                "socket-broker",
                                "ownership-enable-retry-failed",
                                retryError);
                            throw;
                        }
                    }
                    catch (Exception wakeError)
                    {
                        RuntimeDiagnosticsService.Instance.RecordException(
                            "socket-broker",
                            "wake-enable-failed",
                            wakeError);
                        _socket.EnableTransferOwnership(
                            SocketBrokerCoordinator.Instance.TaskId,
                            SocketActivityConnectedStandbyAction.DoNotWake);
                        RuntimeDiagnosticsService.Instance.Write(
                            "socket-broker",
                            "ownership-enabled",
                            "mode=DoNotWake");
                    }
                }

                string service = uri.IsDefaultPort ? "443" : uri.Port.ToString();
                await _socket.ConnectAsync(
                    new HostName(uri.Host),
                    service,
                    SocketProtectionLevel.Tls12);

                await PerformWebSocketHandshakeAsync(uri, headers);
                _connection = new RawWebSocketConnection(_socket);
                _connected = true;
                _ownedByBroker = false;
                _ownershipState.Owner = "foreground";
                _ownershipState.ReconnectRequired = false;
                _ownershipState.LastReason = "transport-connected";
                await BrokerOwnershipStore.SaveAsync(_ownershipState);
                StartReceiveLoop();

                RuntimeDiagnosticsService.Instance.Write(
                    "transport",
                    "streamsocket-websocket-connected",
                    "local=" +
                    FormatEndpoint(
                        _socket.Information.LocalAddress,
                        _socket.Information.LocalPort) +
                    "; remote=" +
                    FormatEndpoint(
                        _socket.Information.RemoteAddress,
                        _socket.Information.RemotePort) +
                    "; socketId=" + _socketId);
            }
            finally
            {
                await lease.ReleaseAsync();
            }
        }

        private static bool IsElementNotFound(Exception error)
        {
            return error != null &&
                   unchecked((uint)error.HResult) == 0x80070490u;
        }

        public Task SendAsync(byte[] data)
        {
            if (!_connected || _ownedByBroker || _transferring || _connection == null)
            {
                throw new InvalidOperationException(_ownedByBroker
                    ? "Socket is owned by the Windows Socket Broker"
                    : _transferring
                        ? "Socket is being transferred to the Windows Socket Broker"
                        : "Transport is not connected");
            }
            return _connection.SendBinaryAsync(data ?? new byte[0], CancellationToken.None);
        }

        public async Task<bool> TransferToBrokerAsync(
            string reason,
            Func<string, Task> beforeTransfer)
        {
            await _ownershipLock.WaitAsync();
            BrokerInterprocessLease lease = null;
            StreamSocket socket = null;
            RawWebSocketConnection connection = null;
            bool streamsDetached = false;
            try
            {
                if (!_connected || _ownedByBroker || _socket == null)
                {
                    return _ownedByBroker;
                }
                if (!SocketBrokerCoordinator.Instance.IsReady)
                {
                    return false;
                }

                lease = await BrokerInterprocessLock.AcquireAsync(
                    "foreground:transfer",
                    TimeSpan.FromSeconds(8),
                    CancellationToken.None);
                if (lease == null)
                {
                    RuntimeDiagnosticsService.Instance.Write(
                        "socket-broker",
                        "ownership-transfer-lock-timeout");
                    return false;
                }

                EnsureUniqueSocketId();
                socket = _socket;
                connection = _connection;
                _transferring = true;
                EnsureOwnershipState("transferring", reason);
                await BrokerOwnershipStore.SaveAsync(_ownershipState);

                RuntimeDiagnosticsService.Instance.Write(
                    "socket-broker",
                    "ownership-quiesce-start",
                    "reason=" + (reason ?? string.Empty) +
                    "; id=" + _socketId);

                // TransferOwnership requires all foreground I/O to be quiesced first.
                // Do not cancel the managed receive token here: cancelling DataReader.LoadAsync
                // through AsTask(token), then disposing the token before the operation finishes,
                // can invalidate the WinRT stream on Windows 10 Mobile. Cancel the socket I/O
                // through the API intended for broker transfer and observe the receive task fully.
                try
                {
                    await socket.CancelIOAsync();
                    RuntimeDiagnosticsService.Instance.Write(
                        "socket-broker",
                        "ownership-cancel-io-complete");
                }
                catch (Exception cancelError)
                {
                    RuntimeDiagnosticsService.Instance.RecordException(
                        "socket-broker",
                        "ownership-cancel-io-failed",
                        cancelError);
                    throw;
                }

                bool receiveStopped = await WaitForReceiveLoopAsync(5000);
                if (!receiveStopped)
                {
                    RuntimeDiagnosticsService.Instance.Write(
                        "socket-broker",
                        "ownership-receive-loop-timeout");
                    throw new TimeoutException("Receive loop did not stop before socket ownership transfer");
                }

                if (connection != null)
                {
                    bool writerIdle = await connection.WaitForWriteIdleAsync(3000);
                    if (!writerIdle)
                    {
                        RuntimeDiagnosticsService.Instance.Write(
                            "socket-broker",
                            "ownership-write-loop-timeout");
                        throw new TimeoutException("WebSocket writer did not become idle before ownership transfer");
                    }
                    connection.DetachStreams();
                    streamsDetached = true;
                }

                RuntimeDiagnosticsService.Instance.Write(
                    "socket-broker",
                    "ownership-streams-detached");

                if (beforeTransfer != null)
                {
                    await beforeTransfer(_socketId);
                    RuntimeDiagnosticsService.Instance.Write(
                        "socket-broker",
                        "ownership-pretransfer-state-saved",
                        "id=" + _socketId);
                }

                // Keep the StreamSocket wrapper alive and untouched until ownership has
                // actually moved to the broker. Disposing the DataReader/DataWriter wrappers
                // happens only after this call succeeds.
                try
                {
                    socket.TransferOwnership(_socketId);
                }
                catch (Exception collision) when (IsAlreadyExists(collision))
                {
                    string previousId = _socketId;
                    _socketId = BrokerOwnershipStore.CreateSocketId();
                    EnsureOwnershipState("transferring", "socket-id-collision-retry");
                    if (beforeTransfer != null)
                    {
                        await beforeTransfer(_socketId);
                    }
                    await BrokerOwnershipStore.SaveAsync(_ownershipState);
                    RuntimeDiagnosticsService.Instance.Write(
                        "socket-broker",
                        "ownership-socket-id-rotated",
                        "old=" + previousId + "; new=" + _socketId);
                    socket.TransferOwnership(_socketId);
                }

                _socket = null;
                _connection = null;
                _ownedByBroker = true;
                _transferring = false;
                EnsureOwnershipState("broker", reason);
                await BrokerOwnershipStore.SaveAsync(_ownershipState);

                if (connection != null)
                {
                    try { connection.Dispose(); } catch { }
                }

                RuntimeDiagnosticsService.Instance.Write(
                    "socket-broker",
                    "ownership-transferred",
                    "reason=" + (reason ?? string.Empty) + "; id=" + _socketId);
                await BrokerLog.AppendAsync(
                    "foreground",
                    "ownership-transferred reason=" + reason + " id=" + _socketId);
                return true;
            }
            catch (Exception ex)
            {
                _transferring = false;
                RuntimeDiagnosticsService.Instance.RecordException("socket-broker", "ownership-transfer-failed", ex);
                await BrokerLog.AppendAsync(
                    "foreground",
                    "ownership-transfer-failed error=" + ex.GetType().Name +
                    " hresult=0x" + ex.HResult.ToString("X8"));
                try
                {
                    EnsureOwnershipState("foreground", "transfer-failed");
                    await BrokerOwnershipStore.SaveAsync(_ownershipState);
                }
                catch
                {
                }

                // If ownership was not transferred and the socket remains usable, rebuild
                // the framing wrappers and resume the foreground receive loop. This prevents
                // a failed broker handoff from silently leaving a connected-but-deaf socket.
                if (!_ownedByBroker && socket != null && ReferenceEquals(_socket, socket))
                {
                    try
                    {
                        if (!streamsDetached && connection != null)
                        {
                            connection.DetachStreams();
                        }
                        if (connection != null)
                        {
                            connection.Dispose();
                        }
                        _connection = new RawWebSocketConnection(socket);
                        _connected = true;
                        StartReceiveLoop();
                        RuntimeDiagnosticsService.Instance.Write(
                            "socket-broker",
                            "ownership-failure-foreground-restored");
                    }
                    catch (Exception restoreError)
                    {
                        _connected = false;
                        RuntimeDiagnosticsService.Instance.RecordException(
                            "socket-broker",
                            "ownership-failure-restore-failed",
                            restoreError);
                        RaiseClosed(1006, "Socket broker ownership transfer failed", restoreError);
                    }
                }
                return false;
            }
            finally
            {
                if (lease != null)
                {
                    await lease.ReleaseAsync();
                }
                _ownershipLock.Release();
            }
        }

        internal async Task<bool> AttachExistingBrokerSocketAsync()
        {
            await _ownershipLock.WaitAsync();
            BrokerInterprocessLease lease = null;
            try
            {
                lease = await BrokerInterprocessLock.AcquireAsync(
                    "foreground:cold-attach",
                    TimeSpan.FromSeconds(8),
                    CancellationToken.None);
                if (lease == null)
                {
                    return false;
                }
                return await AttachExistingBrokerSocketCoreAsync();
            }
            finally
            {
                if (lease != null)
                {
                    await lease.ReleaseAsync();
                }
                _ownershipLock.Release();
            }
        }

        private async Task<bool> AttachExistingBrokerSocketCoreAsync()
        {
            bool brokerReady = await SocketBrokerCoordinator.Instance.EnsureReadyAsync();
            if (!brokerReady)
            {
                return false;
            }

            BrokerOwnershipState persistedState = await BrokerOwnershipStore.LoadAsync();
            if (!BrokerOwnershipStore.IsManagedSocketId(_socketId) &&
                persistedState != null &&
                BrokerOwnershipStore.IsManagedSocketId(persistedState.SocketId))
            {
                _socketId = persistedState.SocketId;
            }

            if (!BrokerOwnershipStore.IsManagedSocketId(_socketId))
            {
                _socketId = SocketActivityInformation.AllSockets.Keys
                    .FirstOrDefault(BrokerOwnershipStore.IsManagedSocketId);
            }

            SocketActivityInformation information = null;
            for (int attempt = 0; attempt < 16; attempt++)
            {
                if (BrokerOwnershipStore.IsManagedSocketId(_socketId) &&
                    SocketActivityInformation.AllSockets.TryGetValue(_socketId, out information) &&
                    information != null &&
                    information.StreamSocket != null)
                {
                    break;
                }

                if (attempt == 4)
                {
                    string discovered = SocketActivityInformation.AllSockets.Keys
                        .FirstOrDefault(BrokerOwnershipStore.IsManagedSocketId);
                    if (!string.IsNullOrEmpty(discovered))
                    {
                        _socketId = discovered;
                    }
                }
                await Task.Delay(125);
            }

            if (information == null || information.StreamSocket == null)
            {
                RuntimeDiagnosticsService.Instance.Write(
                    "socket-broker",
                    "attach-existing-missing",
                    "id=" + (_socketId ?? string.Empty));
                return false;
            }

            _socketId = information.Id;
            _socket = information.StreamSocket;
            _connection = new RawWebSocketConnection(_socket);
            _ownedByBroker = false;
            _connected = true;
            _ownershipState = persistedState != null &&
                              string.Equals(
                                  persistedState.SocketId,
                                  _socketId,
                                  StringComparison.Ordinal)
                ? persistedState
                : BrokerOwnershipStore.Create(
                    _socketId,
                    _generationId,
                    "foreground",
                    "attach-existing");
            _ownershipState.Owner = "foreground";
            _ownershipState.ReconnectRequired = false;
            _ownershipState.LastReason = "attach-existing";
            await BrokerOwnershipStore.SaveAsync(_ownershipState);

            int pendingCount = await BrokerFrameJournal.DrainAsync(
                frame => RaiseMessageReceivedAsync(frame, isBrokerReplay: true));
            RuntimeDiagnosticsService.Instance.Write(
                "socket-broker",
                "attach-existing-complete",
                "pendingFrames=" + pendingCount + "; id=" + _socketId);
            await BrokerLog.AppendAsync(
                "foreground",
                "attach-existing pendingFrames=" + pendingCount + " id=" + _socketId);

            StartReceiveLoop();
            return true;
        }

        public async Task<bool> ReclaimFromBrokerAsync()
        {
            await _ownershipLock.WaitAsync();
            BrokerInterprocessLease lease = null;
            try
            {
                if (!_ownedByBroker)
                {
                    return _connected && _socket != null;
                }

                lease = await BrokerInterprocessLock.AcquireAsync(
                    "foreground:reclaim",
                    TimeSpan.FromSeconds(8),
                    CancellationToken.None);
                if (lease == null)
                {
                    return false;
                }

                bool attached = await AttachExistingBrokerSocketCoreAsync();
                if (attached)
                {
                    RuntimeDiagnosticsService.Instance.Write(
                        "socket-broker",
                        "ownership-reclaimed",
                        "id=" + _socketId);
                }
                return attached;
            }
            catch (Exception ex)
            {
                RuntimeDiagnosticsService.Instance.RecordException("socket-broker", "ownership-reclaim-failed", ex);
                await BrokerLog.AppendAsync("foreground", "ownership-reclaim-failed error=" + ex.Message);
                return false;
            }
            finally
            {
                if (lease != null)
                {
                    await lease.ReleaseAsync();
                }
                _ownershipLock.Release();
            }
        }

        public async Task CloseAsync(ushort code, string reason)
        {
            await _ownershipLock.WaitAsync();
            BrokerInterprocessLease lease = null;
            try
            {
                lease = await BrokerInterprocessLock.AcquireAsync(
                    "foreground:close",
                    TimeSpan.FromSeconds(8),
                    CancellationToken.None);
                if (lease == null)
                {
                    RuntimeDiagnosticsService.Instance.Write(
                        "socket-broker",
                        "ownership-close-lock-timeout");
                    return;
                }
                _connected = false;
                StopReceiveLoop();

                if (_ownedByBroker)
                {
                    await SocketBrokerCoordinator.DisposeBrokerSocketAsync(_socketId);
                    _ownedByBroker = false;
                    await BrokerOwnershipStore.ClearAsync();
                    return;
                }

                if (_connection != null)
                {
                    try { await _connection.SendCloseAsync(code, reason ?? string.Empty, CancellationToken.None); } catch { }
                }
                if (_socket != null)
                {
                    try { await _socket.CancelIOAsync(); } catch { }
                }
                await WaitForReceiveLoopAsync(5000);
                DisposeSocketOnly();
            }
            finally
            {
                if (lease != null)
                {
                    await lease.ReleaseAsync();
                }
                _ownershipLock.Release();
            }
        }

        private void StartReceiveLoop()
        {
            CleanupCompletedReceiveLoop();
            if (_receiveTask != null && !_receiveTask.IsCompleted)
            {
                RuntimeDiagnosticsService.Instance.Write(
                    "transport",
                    "receive-loop-start-skipped",
                    "reason=already-running");
                return;
            }

            _receiveCts = new CancellationTokenSource();
            CancellationToken token = _receiveCts.Token;
            _receiveTask = Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested && _connected && !_ownedByBroker && !_transferring)
                    {
                        RawWebSocketMessage message = await _connection.ReadMessageAsync(token);
                        Interlocked.Increment(ref _activityCount);
                        if (message.Type == RawWebSocketMessageType.Close)
                        {
                            _connected = false;
                            RaiseClosed(message.CloseCode, message.CloseReason, null);
                            return;
                        }
                        if (message.Type == RawWebSocketMessageType.Binary)
                        {
                            await RaiseMessageReceivedAsync(message.Payload);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    if (!_transferring && !_ownedByBroker && _connected)
                    {
                        _connected = false;
                        RaiseClosed(1006, "StreamSocket receive loop failed", ex);
                    }
                }
            });
        }

        private void StopReceiveLoop()
        {
            try { _receiveCts?.Cancel(); } catch { }
        }

        private async Task<bool> WaitForReceiveLoopAsync(int timeoutMilliseconds = 5000)
        {
            Task receive = _receiveTask;
            if (receive == null)
            {
                CleanupCompletedReceiveLoop();
                return true;
            }

            Task completed;
            try
            {
                completed = await Task.WhenAny(receive, Task.Delay(timeoutMilliseconds));
            }
            catch
            {
                return false;
            }

            if (!ReferenceEquals(completed, receive))
            {
                // Never dispose the CancellationTokenSource while LoadAsync may still be
                // using its token. Leave the task referenced so its completion is observed.
                return false;
            }

            try
            {
                await receive;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                // The receive loop normally absorbs transport exceptions. Observe any
                // remaining fault here so it cannot surface later on the finalizer thread.
                RuntimeDiagnosticsService.Instance.RecordException(
                    "transport",
                    "receive-loop-observed-fault",
                    ex);
            }

            CleanupCompletedReceiveLoop();
            return true;
        }

        private void CleanupCompletedReceiveLoop()
        {
            Task receive = _receiveTask;
            if (receive != null && !receive.IsCompleted)
            {
                return;
            }

            try { _receiveCts?.Dispose(); } catch { }
            _receiveCts = null;
            _receiveTask = null;
        }

        private Task RaiseMessageReceivedAsync(byte[] data)
        {
            return RaiseMessageReceivedAsync(data, isBrokerReplay: false);
        }

        private async Task RaiseMessageReceivedAsync(byte[] data, bool isBrokerReplay)
        {
            var handler = MessageReceived;
            if (handler == null) return;
            var args = new TransportMessageEventArgs
            {
                Data = data ?? new byte[0],
                IsBrokerReplay = isBrokerReplay
            };
            foreach (var subscriber in handler.GetInvocationList())
            {
                var asyncHandler = subscriber as Func<object, TransportMessageEventArgs, Task>;
                if (asyncHandler != null)
                {
                    await asyncHandler(this, args);
                }
            }
        }

        private void RaiseClosed(ushort code, string reason, Exception error)
        {
            Closed?.Invoke(this, new TransportClosedEventArgs
            {
                Code = code,
                Reason = reason,
                Error = error
            });
        }

        private async Task PerformWebSocketHandshakeAsync(Uri uri, IDictionary<string, string> headers)
        {
            byte[] keyBytes = new byte[16];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create())
            {
                random.GetBytes(keyBytes);
            }
            string key = Convert.ToBase64String(keyBytes);
            string path = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;

            var request = new StringBuilder();
            request.Append("GET ").Append(path).Append(" HTTP/1.1\r\n");
            request.Append("Host: ").Append(uri.Host).Append("\r\n");
            request.Append("Upgrade: websocket\r\n");
            request.Append("Connection: Upgrade\r\n");
            request.Append("Sec-WebSocket-Key: ").Append(key).Append("\r\n");
            request.Append("Sec-WebSocket-Version: 13\r\n");
            request.Append("Pragma: no-cache\r\n");
            request.Append("Cache-Control: no-cache\r\n");
            if (headers != null)
            {
                foreach (var pair in headers)
                {
                    if (string.Equals(pair.Key, "Host", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(pair.Key, "Connection", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(pair.Key, "Upgrade", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(pair.Key, "Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(pair.Key, "Sec-WebSocket-Version", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    request.Append(pair.Key).Append(": ").Append(pair.Value).Append("\r\n");
                }
            }
            request.Append("\r\n");

            var writer = new DataWriter(_socket.OutputStream)
            {
                UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding.Utf8
            };
            writer.WriteString(request.ToString());
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
            writer.Dispose();

            string response = await ReadHttpHeadersOneByteAtATimeAsync(_socket.InputStream, 32768);
            string firstLine = response.Split(new[] { "\r\n" }, StringSplitOptions.None).FirstOrDefault() ?? string.Empty;
            if (firstLine.IndexOf(" 101 ", StringComparison.Ordinal) < 0 &&
                !firstLine.EndsWith(" 101", StringComparison.Ordinal))
            {
                throw new IOException("WebSocket upgrade rejected: " + firstLine);
            }

            string expectedAccept;
            using (SHA1 sha1 = SHA1.Create())
            {
                expectedAccept = Convert.ToBase64String(
                    sha1.ComputeHash(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            }

            string actualAccept = null;
            string[] lines = response.Split(new[] { "\r\n" }, StringSplitOptions.None);
            foreach (string line in lines)
            {
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string name = line.Substring(0, colon).Trim();
                if (string.Equals(name, "Sec-WebSocket-Accept", StringComparison.OrdinalIgnoreCase))
                {
                    actualAccept = line.Substring(colon + 1).Trim();
                    break;
                }
            }

            if (!string.Equals(expectedAccept, actualAccept, StringComparison.Ordinal))
            {
                throw new IOException("Invalid Sec-WebSocket-Accept response");
            }
        }

        private static async Task<string> ReadHttpHeadersOneByteAtATimeAsync(IInputStream input, int maxBytes)
        {
            var bytes = new List<byte>(1024);
            var reader = new DataReader(input)
            {
                InputStreamOptions = InputStreamOptions.Partial
            };
            try
            {
                while (bytes.Count < maxBytes)
                {
                    uint loaded = await reader.LoadAsync(1);
                    if (loaded == 0) throw new EndOfStreamException("TLS stream closed during WebSocket upgrade");
                    byte value = reader.ReadByte();
                    bytes.Add(value);
                    int count = bytes.Count;
                    if (count >= 4 &&
                        bytes[count - 4] == 13 && bytes[count - 3] == 10 &&
                        bytes[count - 2] == 13 && bytes[count - 1] == 10)
                    {
                        return Encoding.UTF8.GetString(bytes.ToArray(), 0, bytes.Count);
                    }
                }
                throw new IOException("WebSocket response headers exceed safety limit");
            }
            finally
            {
                try { reader.DetachStream(); } catch { }
                reader.Dispose();
            }
        }

        private static string FormatEndpoint(HostName address, string port)
        {
            return (address == null ? "?" : address.DisplayName) + ":" + (port ?? "?");
        }

        private void EnsureUniqueSocketId()
        {
            bool legacy = string.Equals(
                _socketId,
                SocketBrokerConstants.LegacySocketId,
                StringComparison.Ordinal) ||
                          string.Equals(
                              _socketId,
                              SocketBrokerConstants.RegressionInProcessSocketId,
                              StringComparison.Ordinal);
            bool collision = BrokerOwnershipStore.IsManagedSocketId(_socketId) &&
                             SocketActivityInformation.AllSockets.ContainsKey(_socketId);
            if (!BrokerOwnershipStore.IsManagedSocketId(_socketId) || legacy || collision)
            {
                string previous = _socketId;
                _socketId = BrokerOwnershipStore.CreateSocketId();
                RuntimeDiagnosticsService.Instance.Write(
                    "socket-broker",
                    "ownership-socket-id-allocated",
                    "previous=" + (previous ?? string.Empty) +
                    "; current=" + _socketId +
                    "; legacy=" + legacy +
                    "; collision=" + collision);
            }
        }

        private void EnsureOwnershipState(string owner, string reason)
        {
            if (_ownershipState == null)
            {
                _ownershipState = BrokerOwnershipStore.Create(
                    _socketId,
                    _generationId,
                    owner,
                    reason);
            }
            else
            {
                _ownershipState.SocketId = _socketId;
                if (string.IsNullOrWhiteSpace(_ownershipState.Generation))
                {
                    _ownershipState.Generation = _generationId;
                }
                _ownershipState.Owner = owner ?? string.Empty;
                _ownershipState.LastReason = reason ?? string.Empty;
                _ownershipState.ReconnectRequired = false;
            }
        }

        private static bool IsAlreadyExists(Exception error)
        {
            return error != null &&
                   error.HResult == unchecked((int)0x800700B7);
        }

        private void DisposeSocketOnly()
        {
            try { _connection?.Dispose(); } catch { }
            _connection = null;
            try { _socket?.Dispose(); } catch { }
            _socket = null;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(StreamSocketWebSocketTransport));
        }

        private static async Task DisposeBrokerSocketWithLeaseAsync(
            string socketId)
        {
            BrokerInterprocessLease lease = null;
            try
            {
                lease = await BrokerInterprocessLock.AcquireAsync(
                    "foreground:dispose",
                    TimeSpan.FromSeconds(8),
                    CancellationToken.None);
                if (lease == null)
                {
                    await BrokerLog.AppendAsync(
                        "foreground",
                        "broker-dispose-lock-timeout id=" + (socketId ?? string.Empty));
                    return;
                }

                await SocketBrokerCoordinator.DisposeBrokerSocketAsync(socketId);
            }
            catch (Exception ex)
            {
                await BrokerLog.AppendAsync(
                    "foreground",
                    "broker-dispose-failed id=" + (socketId ?? string.Empty) +
                    " error=" + ex.GetType().Name +
                    " hresult=0x" + ex.HResult.ToString("X8"));
            }
            finally
            {
                if (lease != null)
                {
                    await lease.ReleaseAsync();
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _connected = false;
            StopReceiveLoop();

            Task receive = _receiveTask;
            if (receive != null)
            {
                receive.ContinueWith(
                    task =>
                    {
                        try { var ignored = task.Exception; } catch { }
                        CleanupCompletedReceiveLoop();
                    },
                    TaskScheduler.Default);
            }

            if (_ownedByBroker)
            {
                _ = DisposeBrokerSocketWithLeaseAsync(_socketId);
                _ownedByBroker = false;
            }
            DisposeSocketOnly();
            // Do not dispose the ownership lock or receive CTS synchronously while an
            // asynchronous broker handoff/read may still be unwinding.
        }
    }
}
