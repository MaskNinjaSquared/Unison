// =============================================================================
// ConnectionHandler
//
// The heart of the socket layer and the replacement for what WhatsAppService and
// SocketClient do today at the wire level. It connects, performs the Noise
// handshake, frames and unframes binary nodes, correlates queries with their
// replies, and keeps the connection alive.
//
// What it deliberately does NOT do is the point of the whole refactor: it never
// calls a UseCase, never holds domain state, and never decides when to
// reconnect. Features subscribe to the Dispatcher, state lives in the host, and
// reconnect policy reacts to ConnectionUpdate - the three responsibilities whose
// absence keeps this class from becoming the next god class.
//
// Ports: rc14 makeSocket in src/Socket/socket.ts
// =============================================================================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Unison.Baileys.Client;
using Unison.Baileys.Crypto;
using Unison.Baileys.Protocol;
using Unison.Socket.Abstractions;
using Unison.Socket.Events;

namespace Unison.Socket.Session
{
    /// <summary>
    /// Owns the wire: transport, Noise handshake, framing, request/response correlation and
    /// keep-alive. It publishes what it sees and answers what it is asked, and deliberately
    /// knows nothing about chats, messages, groups or contacts.
    /// </summary>
    /// <remarks>
    /// Two rules keep this class from growing into the previous god class:
    /// it never calls a UseCase (inbound features register on <see cref="Dispatcher"/> instead),
    /// and it holds no collection of domain state. Reconnect policy also lives outside, in the
    /// host reacting to <see cref="WaEventKind.ConnectionUpdate"/>, exactly as rc14 does.
    /// </remarks>
    public sealed class ConnectionHandler : IDisposable
    {
        private readonly IWaTransport _transport;
        private readonly AuthState _auth;
        private readonly SocketConfig _config;
        private readonly IWaEventBus _events;
        private readonly IClientPayloadFactory _payloadFactory;
        private readonly ISocketLog _log;

        private readonly NoiseHandler _noise;
        private readonly KeyPair _ephemeralKeyPair;
        private readonly string _tagPrefix;

        private readonly ConcurrentDictionary<string, TaskCompletionSource<BinaryNode>> _waiters =
            new ConcurrentDictionary<string, TaskCompletionSource<BinaryNode>>(StringComparer.Ordinal);

        private readonly List<Func<Exception, Task>> _endHandlers = new List<Func<Exception, Task>>();
        private readonly object _endHandlersGate = new object();
        private readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);

        private readonly object _dispatchGate = new object();
        private Task _dispatchChain = Task.FromResult(true);

        private TaskCompletionSource<bool> _handshakeCompletion;
        private KeepAlive _keepAlive;
        private long _epoch;
        private long _lastReceivedTicks;
        private long _serverTimeOffsetMs;
        private readonly object _endGate = new object();
        private Exception _endError;
        private bool _closed;
        private bool _closeEmitted;
        private bool _disposed;

        public ConnectionHandler(
            IWaTransport transport,
            AuthState auth,
            IClientPayloadFactory payloadFactory,
            IWaEventBus events,
            SocketConfig config = null,
            ISocketLog log = null)
        {
            if (transport == null)
            {
                throw new ArgumentNullException(nameof(transport));
            }

            if (auth == null)
            {
                throw new ArgumentNullException(nameof(auth));
            }

            if (payloadFactory == null)
            {
                throw new ArgumentNullException(nameof(payloadFactory));
            }

            if (events == null)
            {
                throw new ArgumentNullException(nameof(events));
            }

            _transport = transport;
            _auth = auth;
            _payloadFactory = payloadFactory;
            _events = events;
            _config = config ?? new SocketConfig();
            _log = log ?? NullSocketLog.Instance;

            Dispatcher = new NodeDispatcher(_log);

            // rc14: a fresh ephemeral key pair per connection.
            _ephemeralKeyPair = CryptoUtils.GenerateKeyPair();
            _noise = new NoiseHandler(_ephemeralKeyPair, _auth.RoutingInfo);

            _tagPrefix = GenerateTagPrefix();
        }

        /// <summary>Registration point for inbound feature modules.</summary>
        public NodeDispatcher Dispatcher { get; }

        /// <summary>
        /// Every decoded node, before it is routed. This is an observation point and nothing more:
        /// a host that needs to see the raw traffic - to bridge it to another stack, or to log it -
        /// watches here, and whatever it does has no bearing on how the node is then handled.
        /// </summary>
        public event Action<BinaryNode> NodeReceived;

        public bool IsConnected
        {
            get { return !_closed && _transport.IsConnected && _noise.IsFinished; }
        }

        /// <summary>Server clock skew, applied by callers that stamp outgoing content.</summary>
        public long ServerTimeOffsetMs
        {
            get { return Interlocked.Read(ref _serverTimeOffsetMs); }
        }

        public void UpdateServerTimeOffset(long offsetMs)
        {
            Interlocked.Exchange(ref _serverTimeOffsetMs, offsetMs);
        }

        /// <summary>rc14 generateMessageTag: a per-session prefix plus a monotonic counter.</summary>
        public string GenerateMessageTag()
        {
            var next = Interlocked.Increment(ref _epoch);
            return _tagPrefix + next.ToString();
        }

        public IDisposable RegisterNodeHandler(string route, Func<BinaryNode, Task> handler)
        {
            return Dispatcher.Register(route, handler);
        }

        /// <summary>rc14 registerSocketEndHandler: coordinated cleanup when the socket ends.</summary>
        public void RegisterSocketEndHandler(Func<Exception, Task> handler)
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            lock (_endHandlersGate)
            {
                _endHandlers.Add(handler);
            }
        }

        public async Task ConnectAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            ThrowIfDisposed();

            _handshakeCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            MarkFrameReceived();

            _transport.MessageReceived += OnTransportMessageAsync;
            _transport.Closed += OnTransportClosed;

            await _events.EmitAsync(
                WaEventKind.ConnectionUpdate,
                new ConnectionUpdate { Connection = ConnectionStatus.Connecting }).ConfigureAwait(false);

            var uri = BuildConnectionUri();
            var headers = BuildHeaders();

            await _transport.ConnectAsync(uri, headers).ConfigureAwait(false);
            await SendClientHelloAsync().ConfigureAwait(false);

            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeoutCts.CancelAfter(_config.ConnectTimeout);

                var completed = await Task.WhenAny(
                    _handshakeCompletion.Task,
                    Task.Delay(Timeout.Infinite, timeoutCts.Token)).ConfigureAwait(false);

                if (completed != _handshakeCompletion.Task)
                {
                    throw new WaConnectionException(
                        $"Handshake timed out after {_config.ConnectTimeout.TotalSeconds:F0}s",
                        DisconnectReason.ConnectionLost);
                }

                await _handshakeCompletion.Task.ConfigureAwait(false);
            }

            StartKeepAlive();
        }

        public async Task SendRawAsync(byte[] data)
        {
            if (!_transport.IsConnected)
            {
                throw new WaConnectionException("Connection closed", DisconnectReason.ConnectionClosed);
            }

            var frame = _noise.EncodeFrame(data);

            // Noise counters and framing are order-sensitive, so writes are serialised.
            await _sendGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await _transport.SendAsync(frame).ConfigureAwait(false);
            }
            finally
            {
                _sendGate.Release();
            }
        }

        public Task SendNodeAsync(BinaryNode node)
        {
            if (node == null)
            {
                throw new ArgumentNullException(nameof(node));
            }

            var encoder = new BinaryEncoder();
            return SendRawAsync(encoder.Encode(node));
        }

        /// <summary>
        /// Sends a node and waits for the reply carrying the same id, generating one when absent.
        /// </summary>
        public async Task<BinaryNode> QueryAsync(BinaryNode node, TimeSpan? timeout = null)
        {
            var result = await QueryAllowingErrorAsync(node, timeout).ConfigureAwait(false);
            AssertNodeErrorFree(result);
            return result;
        }

        /// <summary>
        /// Sends a query and hands back the reply as it came, error node included.
        /// </summary>
        /// <remarks>
        /// For the queries whose refusal is an answer rather than a fault - an account with no
        /// picture, a group we left - where turning every one into an exception would cost more
        /// than reading the code off the node. Losing the connection still throws.
        /// </remarks>
        public async Task<BinaryNode> QueryAllowingErrorAsync(BinaryNode node, TimeSpan? timeout = null)
        {
            if (node == null)
            {
                throw new ArgumentNullException(nameof(node));
            }

            if (node.Attrs == null)
            {
                node.Attrs = new Dictionary<string, string>();
            }

            string msgId;
            if (!node.Attrs.TryGetValue("id", out msgId) || string.IsNullOrEmpty(msgId))
            {
                msgId = GenerateMessageTag();
                node.Attrs["id"] = msgId;
            }

            // The waiter is armed before sending so a fast reply cannot be missed.
            var waiter = ArmWaiter(msgId);
            try
            {
                await SendNodeAsync(node).ConfigureAwait(false);
                return await AwaitWaiterAsync(msgId, waiter, timeout, Describe(node)).ConfigureAwait(false);
            }
            finally
            {
                TaskCompletionSource<BinaryNode> removed;
                _waiters.TryRemove(msgId, out removed);
            }
        }

        public async Task<BinaryNode> WaitForMessageAsync(string msgId, TimeSpan? timeout = null)
        {
            if (string.IsNullOrEmpty(msgId))
            {
                throw new ArgumentException("msgId is required", nameof(msgId));
            }

            var waiter = ArmWaiter(msgId);
            try
            {
                return await AwaitWaiterAsync(msgId, waiter, timeout).ConfigureAwait(false);
            }
            finally
            {
                TaskCompletionSource<BinaryNode> removed;
                _waiters.TryRemove(msgId, out removed);
            }
        }

        /// <summary>rc14 end(): closes the transport, runs end handlers, announces the close.</summary>
        /// <remarks>
        /// The server often closes the WebSocket before the already-decoded
        /// <c>stream:error</c> / <c>failure</c> is dispatched. The first <see cref="EndAsync"/>
        /// then wins with a generic <see cref="DisconnectReason.ConnectionClosed"/>, and a later
        /// LoggedOut would be dropped. A more specific reason still replaces the generic one
        /// and is emitted so the host can stop reconnecting.
        /// </remarks>
        public async Task EndAsync(Exception error)
        {
            bool firstClose;
            bool reemit;
            Exception emitError;

            lock (_endGate)
            {
                if (!_closed)
                {
                    _closed = true;
                    _endError = error;
                    firstClose = true;
                    reemit = false;
                    emitError = error;
                }
                else if (ShouldUpgradeClose(_endError, error))
                {
                    var previous = _endError;
                    _endError = error;
                    firstClose = false;
                    reemit = _closeEmitted;
                    emitError = error;
                    _log.Warn(
                        "Upgrading close reason: " + DescribeClose(previous) +
                        " → " + DescribeClose(error));
                }
                else
                {
                    return;
                }
            }

            if (firstClose)
            {
                _log.Info(error != null ? "Connection errored: " + error.Message : "Connection closed");

                if (_keepAlive != null)
                {
                    _keepAlive.Dispose();
                    _keepAlive = null;
                }

                _transport.MessageReceived -= OnTransportMessageAsync;
                _transport.Closed -= OnTransportClosed;

                FailPendingWaiters(error);

                try
                {
                    if (_transport.IsConnected)
                    {
                        await _transport.CloseAsync(1000, "end").ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn("Error closing transport", ex);
                }

                Func<Exception, Task>[] handlers;
                lock (_endHandlersGate)
                {
                    handlers = _endHandlers.ToArray();
                }

                foreach (var handler in handlers)
                {
                    try
                    {
                        await handler(error).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _log.Error("Error in socket end handler", ex);
                    }
                }

                Exception latest;
                lock (_endGate)
                {
                    latest = _endError;
                    _closeEmitted = true;
                }

                await EmitConnectionCloseAsync(latest).ConfigureAwait(false);
                return;
            }

            if (reemit)
            {
                await EmitConnectionCloseAsync(emitError).ConfigureAwait(false);
            }
        }

        private Task EmitConnectionCloseAsync(Exception error)
        {
            var waConnectionError = error as WaConnectionException;
            return _events.EmitAsync(
                WaEventKind.ConnectionUpdate,
                new ConnectionUpdate
                {
                    Connection = ConnectionStatus.Close,
                    LastDisconnect = new DisconnectInfo
                    {
                        Error = error,
                        Date = DateTimeOffset.UtcNow,
                        Reason = waConnectionError != null ? waConnectionError.Reason : (DisconnectReason?)null
                    }
                });
        }

        /// <summary>
        /// LoggedOut/Forbidden beat a generic transport close. A replaced connection or a
        /// pairing restart still beat an unexplained hangup, but never overwrite a logout.
        /// </summary>
        private static bool ShouldUpgradeClose(Exception previous, Exception incoming)
        {
            return CloseSpecificity(incoming) > CloseSpecificity(previous);
        }

        private static int CloseSpecificity(Exception error)
        {
            var connectionError = error as WaConnectionException;
            if (connectionError == null)
            {
                return 0;
            }

            switch (connectionError.Reason)
            {
                case DisconnectReason.LoggedOut:
                case DisconnectReason.Forbidden:
                    return 3;
                case DisconnectReason.ConnectionReplaced:
                case DisconnectReason.RestartRequired:
                    return 2;
                case DisconnectReason.BadSession:
                    return 1;
                default:
                    return 0;
            }
        }

        private static string DescribeClose(Exception error)
        {
            var connectionError = error as WaConnectionException;
            if (connectionError != null)
            {
                return connectionError.Reason + " (" + connectionError.Message + ")";
            }

            return error != null ? error.Message : "closed";
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_keepAlive != null)
            {
                _keepAlive.Dispose();
                _keepAlive = null;
            }

            Dispatcher.Clear();
            _sendGate.Dispose();
        }

        private Uri BuildConnectionUri()
        {
            if (_auth.RoutingInfo == null || _auth.RoutingInfo.Length == 0)
            {
                return _config.WaWebSocketUrl;
            }

            // rc14 appends the routing info as base64url in the ED query parameter.
            var ed = Convert.ToBase64String(_auth.RoutingInfo)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');

            var separator = string.IsNullOrEmpty(_config.WaWebSocketUrl.Query) ? "?" : "&";
            return new Uri(_config.WaWebSocketUrl.AbsoluteUri + separator + "ED=" + ed);
        }

        private IDictionary<string, string> BuildHeaders()
        {
            var headers = new Dictionary<string, string> { { "Origin", _config.Origin } };

            if (!string.IsNullOrEmpty(_config.UserAgent))
            {
                headers["User-Agent"] = _config.UserAgent;
            }

            return headers;
        }

        private async Task SendClientHelloAsync()
        {
            var hello = new global::Proto.HandshakeMessage
            {
                ClientHello = new global::Proto.HandshakeMessage.Types.ClientHello
                {
                    Ephemeral = ByteString.CopyFrom(_ephemeralKeyPair.Public)
                }
            };

            _log.Debug("Sending ClientHello");
            await SendRawAsync(hello.ToByteArray()).ConfigureAwait(false);
        }

        private async Task OnTransportMessageAsync(object sender, WaTransportMessageEventArgs args)
        {
            if (args == null || args.Data == null)
            {
                return;
            }

            try
            {
                await _noise.DecodeFrame(args.Data, OnFrameAsync).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error("Failed to decode inbound frame", ex);
            }
        }

        private async Task OnFrameAsync(byte[] frame)
        {
            MarkFrameReceived();

            if (!_noise.IsFinished)
            {
                await CompleteHandshakeAsync(frame).ConfigureAwait(false);
                return;
            }

            BinaryNode node;
            try
            {
                node = BinaryDecoder.Decode(frame);
            }
            catch (Exception ex)
            {
                _log.Error("Failed to decode binary node", ex);
                return;
            }

            if (node == null)
            {
                return;
            }

            var observer = NodeReceived;
            if (observer != null)
            {
                try
                {
                    observer(node);
                }
                catch (Exception ex)
                {
                    // An observer is a bystander; it must not stop the node being handled.
                    _log.Error("A node observer threw", ex);
                }
            }

            var answeredAQuery = false;

            string msgId;
            if (node.Attrs != null && node.Attrs.TryGetValue("id", out msgId) && !string.IsNullOrEmpty(msgId))
            {
                TaskCompletionSource<BinaryNode> waiter;
                if (_waiters.TryGetValue(msgId, out waiter))
                {
                    answeredAQuery = waiter.TrySetResult(node);
                }
            }

            QueueForDispatch(node, answeredAQuery);
        }

        /// <summary>
        /// Hands a node to the handlers without waiting for them.
        ///
        /// Node's socket keeps delivering messages while a handler awaits, so rc14 can answer a
        /// retry receipt by querying the server from inside the handler. Here the transport reads
        /// the next frame only after the previous one has been handled, so doing the same would
        /// mean waiting on a reply that cannot be read: the connection stalls until every
        /// outstanding query times out and the keep-alive gives up on it.
        ///
        /// Handlers still run one after another, in arrival order, on a chain of their own.
        /// Queries answer from the read loop above, which is what lets a handler wait for one.
        /// </summary>
        private void QueueForDispatch(BinaryNode node, bool answeredAQuery)
        {
            lock (_dispatchGate)
            {
                _dispatchChain = _dispatchChain
                    .ContinueWith(
                        _ => DispatchQueuedAsync(node, answeredAQuery),
                        CancellationToken.None,
                        TaskContinuationOptions.None,
                        TaskScheduler.Default)
                    .Unwrap();
            }
        }

        private async Task DispatchQueuedAsync(BinaryNode node, bool answeredAQuery)
        {
            try
            {
                var triggered = await Dispatcher.DispatchAsync(node).ConfigureAwait(false);
                if (!triggered && !answeredAQuery)
                {
                    _log.Debug("Unhandled node: " + node.Tag);
                }
            }
            catch (Exception ex)
            {
                _log.Error("Failed to handle node " + node.Tag, ex);
            }
        }

        private async Task CompleteHandshakeAsync(byte[] frame)
        {
            try
            {
                var handshake = global::Proto.HandshakeMessage.Parser.ParseFrom(frame);
                if (handshake.ServerHello == null)
                {
                    throw new WaConnectionException("ServerHello missing from handshake message", DisconnectReason.BadSession);
                }

                var serverHello = handshake.ServerHello;
                var keyEnc = _noise.ProcessHandshake(
                    serverHello.Ephemeral.ToByteArray(),
                    serverHello.Static.ToByteArray(),
                    serverHello.Payload.ToByteArray(),
                    _auth.NoiseKey);

                var payload = _payloadFactory.Build(_auth, _config);
                var payloadEnc = _noise.Encrypt(payload.ToByteArray());

                var finish = new global::Proto.HandshakeMessage
                {
                    ClientFinish = new global::Proto.HandshakeMessage.Types.ClientFinish
                    {
                        Static = ByteString.CopyFrom(keyEnc),
                        Payload = ByteString.CopyFrom(payloadEnc)
                    }
                };

                await SendRawAsync(finish.ToByteArray()).ConfigureAwait(false);

                _noise.FinishInit();
                _log.Info("Noise handshake complete");

                var completion = _handshakeCompletion;
                if (completion != null)
                {
                    completion.TrySetResult(true);
                }
            }
            catch (Exception ex)
            {
                _log.Error("Handshake failed", ex);

                var completion = _handshakeCompletion;
                if (completion != null)
                {
                    completion.TrySetException(ex);
                }
            }
        }

        private void OnTransportClosed(object sender, WaTransportClosedEventArgs args)
        {
            var reason = args != null && !string.IsNullOrEmpty(args.Reason) ? args.Reason : "transport closed";
            var error = args != null && args.Error != null
                ? new WaConnectionException(reason, DisconnectReason.ConnectionClosed, args.Error)
                : new WaConnectionException(reason, DisconnectReason.ConnectionClosed);

            var completion = _handshakeCompletion;
            if (completion != null)
            {
                completion.TrySetException(error);
            }

            EndAsync(error).ContinueWith(
                t => _log.Error("Error ending connection after transport close", t.Exception),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        private void StartKeepAlive()
        {
            _keepAlive = new KeepAlive(
                _config.KeepAliveInterval,
                _config.KeepAliveGrace,
                () => _transport.IsConnected,
                GetLastReceived,
                SendPingAsync,
                EndAsync,
                _log);

            _keepAlive.Start();
        }

        private Task SendPingAsync()
        {
            var ping = new BinaryNode(
                "iq",
                new Dictionary<string, string>
                {
                    { "id", GenerateMessageTag() },
                    { "to", WA.S_WHATSAPP_NET },
                    { "type", "get" },
                    { "xmlns", "w:p" }
                },
                new List<BinaryNode> { new BinaryNode("ping") });

            return QueryAsync(ping, _config.KeepAliveInterval);
        }

        private TaskCompletionSource<BinaryNode> ArmWaiter(string msgId)
        {
            var waiter = new TaskCompletionSource<BinaryNode>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters[msgId] = waiter;
            return waiter;
        }

        private async Task<BinaryNode> AwaitWaiterAsync(
            string msgId,
            TaskCompletionSource<BinaryNode> waiter,
            TimeSpan? timeout,
            string description = null)
        {
            var effectiveTimeout = timeout ?? _config.DefaultQueryTimeout;

            using (var cts = new CancellationTokenSource())
            {
                var delay = Task.Delay(effectiveTimeout, cts.Token);
                var completed = await Task.WhenAny(waiter.Task, delay).ConfigureAwait(false);

                if (completed != waiter.Task)
                {
                    // A bare id says nothing about what was asked, and by the time one of these
                    // shows up the interesting part is usually whether the whole connection went
                    // quiet: a lone timeout is a refused query, a pile of them with a stale last
                    // frame is a socket that stopped being read.
                    var lastFrame = GetLastReceived();
                    var silence = lastFrame.HasValue
                        ? (DateTimeOffset.UtcNow - lastFrame.Value).TotalSeconds.ToString("F1") + "s ago"
                        : "never";

                    throw new WaConnectionException(
                        "Timed out after " + effectiveTimeout.TotalSeconds.ToString("F0") + "s waiting for a reply to " +
                        (description ?? "'" + msgId + "'") +
                        " [id=" + msgId +
                        "; pendingQueries=" + _waiters.Count +
                        "; lastFrame=" + silence +
                        "; connected=" + IsConnected + "]",
                        DisconnectReason.ConnectionLost);
                }

                cts.Cancel();
                return await waiter.Task.ConfigureAwait(false);
            }
        }

        /// <summary>Names a node the way the server sees it, for messages a human has to read.</summary>
        private static string Describe(BinaryNode node)
        {
            if (node == null)
            {
                return "an unknown node";
            }

            var description = "<" + node.Tag;
            foreach (var name in new[] { "xmlns", "type", "to" })
            {
                var value = node.GetAttribute(name);
                if (!string.IsNullOrEmpty(value))
                {
                    description += " " + name + "=" + value;
                }
            }

            var child = node.GetAllChildren();
            if (child != null && child.Count > 0 && !string.IsNullOrEmpty(child[0].Tag))
            {
                description += "><" + child[0].Tag;
            }

            return description + ">";
        }

        private void FailPendingWaiters(Exception error)
        {
            var failure = error ?? new WaConnectionException("Connection closed", DisconnectReason.ConnectionClosed);

            foreach (var pair in _waiters)
            {
                pair.Value.TrySetException(failure);
            }

            _waiters.Clear();
        }

        private void MarkFrameReceived()
        {
            Interlocked.Exchange(ref _lastReceivedTicks, DateTimeOffset.UtcNow.UtcTicks);
        }

        private DateTimeOffset? GetLastReceived()
        {
            var ticks = Interlocked.Read(ref _lastReceivedTicks);
            if (ticks == 0)
            {
                return null;
            }

            return new DateTimeOffset(ticks, TimeSpan.Zero);
        }

        /// <summary>rc14 assertNodeErrorFree.</summary>
        private static void AssertNodeErrorFree(BinaryNode node)
        {
            if (node == null)
            {
                return;
            }

            var errorNode = node.GetChild("error");
            if (errorNode == null)
            {
                return;
            }

            var code = errorNode.GetAttribute("code");
            var text = errorNode.GetAttribute("text");

            int parsedCode;
            var reason = int.TryParse(code, out parsedCode)
                ? (DisconnectReason)parsedCode
                : DisconnectReason.BadSession;

            // The refusal alone reads as "not-authorized" with no hint of what was refused, so
            // the reply's own address and id travel with it.
            var from = node.GetAttribute("from");
            var id = node.GetAttribute("id");

            var message = (string.IsNullOrEmpty(text) ? "refused" : text) +
                " (code " + (string.IsNullOrEmpty(code) ? "none" : code) + ")" +
                " answering <" + node.Tag + ">" +
                (string.IsNullOrEmpty(from) ? string.Empty : " from " + from) +
                (string.IsNullOrEmpty(id) ? string.Empty : " [id=" + id + "]");

            throw new WaConnectionException(message, reason);
        }

        /// <summary>rc14 generateMdTagPrefix.</summary>
        private static string GenerateTagPrefix()
        {
            var bytes = CryptoUtils.RandomBytes(2);
            var seed = (bytes[0] << 8) | bytes[1];
            return seed.ToString() + "." + (DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 1000).ToString() + "-";
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ConnectionHandler));
            }
        }
    }
}
