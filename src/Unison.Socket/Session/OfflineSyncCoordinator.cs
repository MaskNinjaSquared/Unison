// =============================================================================
// OfflineSyncCoordinator
//
// Runs the handshake around the backlog the server replays after connecting.
//
// The server offers a preview, we ask for a batch of a hundred, it sends them,
// and then it tells us how many it sent. That last node is the only reliable
// signal that the backlog is done - and it is what releases the event buffer, so
// the whole burst reaches the app as one batch instead of a hundred separate
// updates that each redraw the chat list.
//
// This is what the current code approximates with an idle monitor, a two-second
// settle timer and a gap-detection retry: it never sees the "done" node as
// authoritative, so it has to guess. Here the guessing is unnecessary.
//
// Ports: rc14 the CB:ib,,offline_preview and CB:ib,,offline handlers plus the
// connect-time ev.buffer() in src/Socket/socket.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Baileys.Client;
using Unison.Baileys.Protocol;
using Unison.Socket.Abstractions;
using Unison.Socket.Events;

namespace Unison.Socket.Session
{
    public sealed class OfflineSyncCoordinator : IDisposable
    {
        /// <summary>How many pending stanzas to ask for at a time.</summary>
        private const int BatchSize = 100;

        /// <summary>
        /// How long to wait for the server to say the backlog is drained before releasing the
        /// buffer anyway. Holding events forever because one node never arrived would look like
        /// a frozen app, which is worse than a batch that arrives early.
        /// </summary>
        private static readonly TimeSpan BufferSafetyTimeout = TimeSpan.FromSeconds(20);

        private readonly ConnectionHandler _connection;
        private readonly IWaEventBus _events;
        private readonly AuthState _auth;
        private readonly ISocketLog _log;
        private readonly List<IDisposable> _routes = new List<IDisposable>();

        private readonly object _gate = new object();

        private bool _buffering;
        private bool _disposed;

        public OfflineSyncCoordinator(
            ConnectionHandler connection,
            IWaEventBus events,
            AuthState auth,
            ISocketLog log = null)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            if (events == null)
            {
                throw new ArgumentNullException(nameof(events));
            }

            _connection = connection;
            _events = events;
            _auth = auth;
            _log = log ?? NullSocketLog.Instance;
        }

        /// <summary>True once the server has confirmed the backlog is drained.</summary>
        public bool ReceivedPendingNotifications { get; private set; }

        /// <summary>How many stanzas the server said it replayed.</summary>
        public int ReplayedCount { get; private set; }

        public void Attach()
        {
            _routes.Add(_connection.Dispatcher.Register("ib,,offline_preview", OnOfflinePreviewAsync));
            _routes.Add(_connection.Dispatcher.Register("ib,,offline", OnOfflineCompleteAsync));
        }

        /// <summary>
        /// Starts buffering, if this is a session that will have a backlog. A first-time login
        /// has nothing to replay, so buffering it would only delay the pairing feedback.
        /// </summary>
        public void BeginBuffering()
        {
            var isKnownDevice = _auth != null && _auth.Me != null && !string.IsNullOrEmpty(_auth.Me.Id);
            if (!isKnownDevice)
            {
                return;
            }

            lock (_gate)
            {
                if (_buffering)
                {
                    return;
                }

                _buffering = true;
            }

            _events.Buffer();
            _log.Debug("[Offline] Buffering events until the backlog is drained");

            var _ = Task.Delay(BufferSafetyTimeout).ContinueWith(t => ReleaseBufferAsync("timed out"));
        }

        /// <summary>Releases the buffer if it is still held. Safe to call more than once.</summary>
        private async Task ReleaseBufferAsync(string reason)
        {
            bool shouldFlush;
            lock (_gate)
            {
                shouldFlush = _buffering;
                _buffering = false;
            }

            if (!shouldFlush)
            {
                return;
            }

            await _events.FlushAsync().ConfigureAwait(false);
            _log.Debug("[Offline] Flushed the initial buffer (" + reason + ")");

            // Timeout must still tell the host the replay window is over. Without this,
            // the chat-list header stays on "Updating..." forever (it only clears on "synced").
            if (string.Equals(reason, "timed out", StringComparison.Ordinal) &&
                !ReceivedPendingNotifications)
            {
                ReceivedPendingNotifications = true;
                _log.Info("[Offline] Safety timeout: treating pending notifications as received");
                await _events.EmitAsync(
                    WaEventKind.ConnectionUpdate,
                    new ConnectionUpdate { ReceivedPendingNotifications = true }).ConfigureAwait(false);
            }
        }

        /// <summary>The server is offering a backlog; ask for the first batch.</summary>
        private async Task OnOfflinePreviewAsync(BinaryNode node)
        {
            _log.Info("[Offline] Backlog announced, requesting a batch of " + BatchSize);

            await _connection.SendNodeAsync(new BinaryNode(
                "ib",
                null,
                new List<BinaryNode>
                {
                    new BinaryNode(
                        "offline_batch",
                        new Dictionary<string, string> { { "count", BatchSize.ToString() } })
                })).ConfigureAwait(false);
        }

        /// <summary>
        /// The backlog is drained. Release the buffer so everything it produced lands as one
        /// batch, and tell the host it can start treating traffic as live.
        /// </summary>
        private async Task OnOfflineCompleteAsync(BinaryNode node)
        {
            var child = node.GetChild("offline");
            int count;
            int.TryParse(child != null ? child.GetAttribute("count") : null, out count);

            ReplayedCount = count;
            ReceivedPendingNotifications = true;

            _log.Info("[Offline] Server replayed " + count + " stanza(s)");

            await ReleaseBufferAsync("backlog drained").ConfigureAwait(false);

            await _events.EmitAsync(
                WaEventKind.ConnectionUpdate,
                new ConnectionUpdate { ReceivedPendingNotifications = true }).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            foreach (var route in _routes)
            {
                route.Dispose();
            }

            _routes.Clear();

            // A buffer left held would strand everything in it.
            bool wasBuffering;
            lock (_gate)
            {
                wasBuffering = _buffering;
                _buffering = false;
            }

            if (wasBuffering)
            {
                var _ = _events.FlushAsync();
            }
        }
    }
}
