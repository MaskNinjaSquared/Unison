// =============================================================================
// OfflineNodeProcessor
//
// Drains the burst of stanzas the server replays after connecting.
//
// The rules are simple and they matter: strictly one at a time, in order, never
// stop because one of them threw, and give the rest of the app a breath every
// few nodes. That last part is why the current code is full of ad-hoc timers and
// idle monitors - it processes the burst inline and then tries to detect when it
// went quiet. Here the queue itself is the thing that knows when it is done.
//
// Ports: rc14 makeOfflineNodeProcessor in src/Utils/offline-node-processor.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Socket.Abstractions;

namespace Unison.Socket.Messages
{
    /// <summary>The stanza classes the server replays offline.</summary>
    public enum OfflineNodeKind
    {
        Message,
        Call,
        Receipt,
        Notification
    }

    public sealed class OfflineNodeProcessor
    {
        private const int DefaultBatchSize = 10;

        private readonly Dictionary<OfflineNodeKind, Func<BinaryNode, Task>> _handlers;
        private readonly Func<bool> _isConnected;
        private readonly ISocketLog _log;
        private readonly int _batchSize;

        private readonly Queue<QueuedNode> _queue = new Queue<QueuedNode>();
        private readonly object _gate = new object();

        private bool _draining;

        /// <param name="isConnected">
        /// Checked between nodes: when the socket drops mid-burst the rest of the queue is
        /// abandoned rather than processed against a dead connection.
        /// </param>
        public OfflineNodeProcessor(
            IDictionary<OfflineNodeKind, Func<BinaryNode, Task>> handlers,
            Func<bool> isConnected,
            ISocketLog log = null,
            int batchSize = DefaultBatchSize)
        {
            if (handlers == null)
            {
                throw new ArgumentNullException(nameof(handlers));
            }

            if (isConnected == null)
            {
                throw new ArgumentNullException(nameof(isConnected));
            }

            _handlers = new Dictionary<OfflineNodeKind, Func<BinaryNode, Task>>(handlers);
            _isConnected = isConnected;
            _log = log ?? NullSocketLog.Instance;
            _batchSize = batchSize > 0 ? batchSize : DefaultBatchSize;
        }

        /// <summary>Number of stanzas still waiting. Zero while idle.</summary>
        public int PendingCount
        {
            get
            {
                lock (_gate)
                {
                    return _queue.Count;
                }
            }
        }

        public bool IsDraining
        {
            get
            {
                lock (_gate)
                {
                    return _draining;
                }
            }
        }

        /// <summary>
        /// Queues a stanza and starts draining if nothing else is. Returns immediately: the
        /// caller is the read loop and must not be held up by handler work.
        /// </summary>
        public void Enqueue(OfflineNodeKind kind, BinaryNode node)
        {
            if (node == null)
            {
                return;
            }

            lock (_gate)
            {
                _queue.Enqueue(new QueuedNode { Kind = kind, Node = node });

                if (_draining)
                {
                    return;
                }

                _draining = true;
            }

            var _ = DrainAsync();
        }

        private async Task DrainAsync()
        {
            var processedInBatch = 0;

            while (true)
            {
                QueuedNode next;
                lock (_gate)
                {
                    if (_queue.Count == 0 || !_isConnected())
                    {
                        _draining = false;
                        return;
                    }

                    next = _queue.Dequeue();
                }

                Func<BinaryNode, Task> handler;
                if (!_handlers.TryGetValue(next.Kind, out handler) || handler == null)
                {
                    _log.Warn("[Offline] No handler for " + next.Kind + " nodes");
                    continue;
                }

                try
                {
                    await handler(next.Node).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // One bad stanza must never strand the rest of the burst.
                    _log.Error("[Offline] Failed to process a " + next.Kind + " node", ex);
                }

                processedInBatch++;
                if (processedInBatch >= _batchSize)
                {
                    processedInBatch = 0;

                    // Hand the thread back so live traffic and the UI are not starved by a
                    // long replay.
                    await Task.Yield();
                }
            }
        }

        private struct QueuedNode
        {
            public OfflineNodeKind Kind;
            public BinaryNode Node;
        }
    }
}
