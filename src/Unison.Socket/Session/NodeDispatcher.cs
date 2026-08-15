// =============================================================================
// NodeDispatcher
//
// The routing table for incoming binary nodes. Feature modules register the
// route they care about ("iq,type:set,pair-device", "message", "ib,,dirty") and
// the dispatcher fans each node out from the most specific route to the least.
//
// This is what replaces the giant switch on node tags in the current service:
// adding a feature means registering a route from its own file, never editing a
// shared dispatch method.
//
// Ports: rc14 the CB: fan-out inside onMessageReceived, src/Socket/socket.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Socket.Abstractions;

namespace Unison.Socket.Session
{
    /// <summary>
    /// Routes an incoming node to handlers registered by tag, attribute and first child, using
    /// the rc14 route grammar. This is a dispatch table only - it knows nothing about what a
    /// message, a receipt or a group is, which is what keeps the handler free of feature logic.
    /// </summary>
    /// <remarks>
    /// Routes, from most to least specific:
    /// <c>tag,attr:value,child</c>, <c>tag,attr:value</c>, <c>tag,attr</c>, <c>tag,,child</c>, <c>tag</c>.
    /// Example: <c>iq,type:set,pair-device</c> or <c>ib,,dirty</c>.
    /// </remarks>
    public sealed class NodeDispatcher
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, List<Func<BinaryNode, Task>>> _routes =
            new Dictionary<string, List<Func<BinaryNode, Task>>>(StringComparer.Ordinal);

        private readonly ISocketLog _log;

        public NodeDispatcher(ISocketLog log = null)
        {
            _log = log ?? NullSocketLog.Instance;
        }

        public IDisposable Register(string route, Func<BinaryNode, Task> handler)
        {
            if (string.IsNullOrEmpty(route))
            {
                throw new ArgumentException("Route is required", nameof(route));
            }

            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            lock (_gate)
            {
                List<Func<BinaryNode, Task>> handlers;
                if (!_routes.TryGetValue(route, out handlers))
                {
                    handlers = new List<Func<BinaryNode, Task>>();
                    _routes[route] = handlers;
                }

                handlers.Add(handler);
            }

            return new Registration(this, route, handler);
        }

        /// <summary>Returns true when at least one handler ran, mirroring rc14 <c>anyTriggered</c>.</summary>
        public async Task<bool> DispatchAsync(BinaryNode node)
        {
            if (node == null)
            {
                return false;
            }

            var triggered = false;

            foreach (var route in RoutesFor(node))
            {
                Func<BinaryNode, Task>[] handlers;
                lock (_gate)
                {
                    List<Func<BinaryNode, Task>> list;
                    if (!_routes.TryGetValue(route, out list) || list.Count == 0)
                    {
                        continue;
                    }

                    handlers = list.ToArray();
                }

                foreach (var handler in handlers)
                {
                    triggered = true;
                    try
                    {
                        await handler(node).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _log.Error($"Node handler for route '{route}' threw", ex);
                    }
                }
            }

            return triggered;
        }

        /// <summary>Route keys for a node, most specific first.</summary>
        public static IEnumerable<string> RoutesFor(BinaryNode node)
        {
            var tag = node.Tag ?? string.Empty;

            var children = node.GetAllChildren();
            var firstChild = children.Count > 0 ? (children[0].Tag ?? string.Empty) : string.Empty;

            if (node.Attrs != null)
            {
                foreach (var attr in node.Attrs)
                {
                    yield return tag + "," + attr.Key + ":" + attr.Value + "," + firstChild;
                    yield return tag + "," + attr.Key + ":" + attr.Value;
                    yield return tag + "," + attr.Key;
                }
            }

            yield return tag + ",," + firstChild;
            yield return tag;
        }

        private void Unregister(string route, Func<BinaryNode, Task> handler)
        {
            lock (_gate)
            {
                List<Func<BinaryNode, Task>> handlers;
                if (!_routes.TryGetValue(route, out handlers))
                {
                    return;
                }

                handlers.Remove(handler);
                if (handlers.Count == 0)
                {
                    _routes.Remove(route);
                }
            }
        }

        internal void Clear()
        {
            lock (_gate)
            {
                _routes.Clear();
            }
        }

        private sealed class Registration : IDisposable
        {
            private readonly NodeDispatcher _owner;
            private readonly string _route;
            private Func<BinaryNode, Task> _handler;

            public Registration(NodeDispatcher owner, string route, Func<BinaryNode, Task> handler)
            {
                _owner = owner;
                _route = route;
                _handler = handler;
            }

            public void Dispose()
            {
                var handler = Interlocked.Exchange(ref _handler, null);
                if (handler != null)
                {
                    _owner.Unregister(_route, handler);
                }
            }
        }
    }
}
