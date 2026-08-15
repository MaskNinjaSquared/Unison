// =============================================================================
// WaEventBatch
//
// The unit of delivery on the event bus: a map of event kind to payload, handed
// to subscribers as one object. Batching is what lets an initial history sync be
// written in a single transaction instead of thousands of individual updates.
//
// Ports: rc14 BaileysEventData in src/Utils/event-buffer.ts
// =============================================================================
using System;
using System.Collections.Generic;

namespace Unison.Socket.Events
{
    /// <summary>
    /// A set of events delivered together. Handlers receive a whole batch so that a sync burst
    /// can be persisted in one transaction instead of one write per event.
    /// </summary>
    public sealed class WaEventBatch
    {
        private readonly Dictionary<WaEventKind, object> _events;

        public WaEventBatch(Dictionary<WaEventKind, object> events)
        {
            if (events == null)
            {
                throw new ArgumentNullException(nameof(events));
            }

            _events = events;
        }

        public static WaEventBatch Single(WaEventKind kind, object payload)
        {
            var map = new Dictionary<WaEventKind, object>(1) { { kind, payload } };
            return new WaEventBatch(map);
        }

        public int Count
        {
            get { return _events.Count; }
        }

        public IEnumerable<WaEventKind> Kinds
        {
            get { return _events.Keys; }
        }

        public bool Contains(WaEventKind kind)
        {
            return _events.ContainsKey(kind);
        }

        public object Get(WaEventKind kind)
        {
            object payload;
            return _events.TryGetValue(kind, out payload) ? payload : null;
        }

        public bool TryGet<T>(WaEventKind kind, out T payload) where T : class
        {
            object raw;
            if (_events.TryGetValue(kind, out raw))
            {
                payload = raw as T;
                return payload != null;
            }

            payload = null;
            return false;
        }
    }
}
